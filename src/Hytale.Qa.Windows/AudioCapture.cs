using System.Runtime.InteropServices;
using Hytale.Qa.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Hytale.Qa.Windows;

public interface IAudioCaptureBackend : IDisposable
{
    AudioCaptureCapability Capability { get; }
    AudioCaptureState State { get; }
    AudioCaptureState Start(ProcessIdentity identity, string destinationPath, Func<SafetyCheck> safetyGuard);
    AudioCaptureArtifact Stop(string captureId);
    void Abort(string reason);
}

/// <summary>
/// Captures only audio rendered by the leased process and its descendants through the Windows
/// application-loopback virtual audio device. It deliberately does not fall back to system-wide
/// loopback because unrelated desktop audio would make QA evidence ambiguous.
/// </summary>
public sealed class ProcessLoopbackAudioCaptureBackend : IAudioCaptureBackend
{
    private const int MinimumBuild = 20348;
    private readonly object sync = new();
    private ActiveCapture? active;
    private AudioCaptureState state;

    public ProcessLoopbackAudioCaptureBackend()
    {
        state = new(null, AudioCaptureStatus.Idle, null, null, null, null, Capability);
    }

    public AudioCaptureCapability Capability => new(
        "wasapi-process-loopback",
        IsSupported ? CaptureAvailability.Available : CaptureAvailability.Unsupported,
        ProcessIsolated: true,
        IncludesChildProcesses: true,
        Formats: ["audio/wav;codec=pcm_s16le"],
        MinimumWindowsBuild: MinimumBuild,
        Limitation: IsSupported
            ? "Captures the leased PID and its child-process render streams as 44.1 kHz stereo 16-bit PCM. A silent target produces valid silence."
            : "Per-process loopback requires Windows 10 build 20348 or later. System-wide loopback is intentionally not used as a fallback.");

    public AudioCaptureState State
    {
        get { lock (sync) return state; }
    }

    private static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    public AudioCaptureState Start(ProcessIdentity identity, string destinationPath, Func<SafetyCheck> safetyGuard)
    {
        ArgumentNullException.ThrowIfNull(safetyGuard);
        if (!IsSupported) throw new NotSupportedException(Capability.Limitation);
        var initialSafety = safetyGuard();
        if (!initialSafety.Safe)
            throw new InvalidOperationException($"Audio capture safety predicate failed: {initialSafety.Code}: {initialSafety.Message}");

        var finalPath = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(finalPath), ".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Process loopback capture destination must use the .wav extension.");
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("Audio destination has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(finalPath)) throw new IOException("Audio destination already exists; evidence is append-only.");

        lock (sync)
        {
            if (active is not null) throw new InvalidOperationException("An audio capture is already active.");
            var captureId = Guid.NewGuid().ToString("N");
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{captureId}.tmp");
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                active = ActiveCapture.Start(identity.ProcessId, captureId, finalPath, temporaryPath,
                    startedAt, safetyGuard, OnAutomaticAbort);
                state = new(captureId, AudioCaptureStatus.Capturing, identity.ProcessId, finalPath,
                    startedAt, null, Capability);
                return state;
            }
            catch
            {
                TryDelete(temporaryPath);
                active = null;
                state = new(captureId, AudioCaptureStatus.Failed, identity.ProcessId, finalPath,
                    startedAt, "activation_failed", Capability);
                throw;
            }
        }
    }

    public AudioCaptureArtifact Stop(string captureId)
    {
        ActiveCapture current;
        lock (sync)
        {
            current = active ?? throw new InvalidOperationException("No audio capture is active.");
            if (!string.Equals(current.CaptureId, captureId, StringComparison.Ordinal))
                throw new InvalidOperationException("Audio capture id does not match the active capture.");
        }

        try
        {
            var artifact = current.Complete(Capability);
            lock (sync)
            {
                active = null;
                state = new(captureId, AudioCaptureStatus.Completed, current.ProcessId,
                    artifact.Path, current.StartedAtUtc, "requested", Capability);
            }
            return artifact;
        }
        catch (Exception ex)
        {
            current.Abort("stop_failed");
            lock (sync)
            {
                active = null;
                state = new(captureId, AudioCaptureStatus.Failed, current.ProcessId,
                    current.FinalPath, current.StartedAtUtc, ex.Message, Capability);
            }
            throw;
        }
    }

    public void Abort(string reason)
    {
        ActiveCapture? current;
        lock (sync)
        {
            current = active;
            active = null;
            if (current is not null)
                state = new(current.CaptureId, AudioCaptureStatus.Aborted, current.ProcessId,
                    current.FinalPath, current.StartedAtUtc, reason, Capability);
        }
        current?.Abort(reason);
    }

    private void OnAutomaticAbort(ActiveCapture capture, string reason)
    {
        lock (sync)
        {
            if (!ReferenceEquals(active, capture)) return;
            active = null;
            state = new(capture.CaptureId, AudioCaptureStatus.Aborted, capture.ProcessId,
                capture.FinalPath, capture.StartedAtUtc, reason, Capability);
        }
    }

    public void Dispose() => Abort("backend_disposed");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Best effort after preserving the primary failure. */ }
    }

    private sealed class ActiveCapture
    {
        private const int SampleRate = 44_100;
        private const short Channels = 2;
        private const short BitsPerSample = 16;
        private const int BlockAlign = Channels * BitsPerSample / 8;
        private const int AverageBytesPerSecond = SampleRate * BlockAlign;
        private const uint AudioClientStreamFlagsLoopback = 0x00020000;
        private const uint AudioClientStreamFlagsEventCallback = 0x00040000;
        private const uint AudioClientStreamFlagsAutoConvertPcm = 0x80000000;
        private const uint AudioClientBufferFlagsSilent = 0x00000002;
        private static readonly Guid AudioCaptureClientGuid = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        private readonly object sync = new();
        private readonly Func<SafetyCheck> safetyGuard;
        private readonly Action<ActiveCapture, string> automaticAbort;
        private readonly CancellationTokenSource cancellation = new();
        private readonly EventWaitHandle sampleReady = new(false, EventResetMode.AutoReset);
        private readonly FileStream output;
        private readonly BinaryWriter writer;
        private IAudioClient? audioClient;
        private IAudioCaptureClient? captureClient;
        private Task? pump;
        private long dataBytes;
        private string? abortReason;
        private bool disposed;

        private ActiveCapture(int processId, string captureId, string finalPath, string temporaryPath,
            DateTimeOffset startedAtUtc, Func<SafetyCheck> safetyGuard,
            Action<ActiveCapture, string> automaticAbort)
        {
            ProcessId = processId;
            CaptureId = captureId;
            FinalPath = finalPath;
            TemporaryPath = temporaryPath;
            StartedAtUtc = startedAtUtc;
            this.safetyGuard = safetyGuard;
            this.automaticAbort = automaticAbort;
            output = new(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            writer = new(output, System.Text.Encoding.UTF8, leaveOpen: true);
            WriteWaveHeader(writer, 0);
        }

        internal int ProcessId { get; }
        internal string CaptureId { get; }
        internal string FinalPath { get; }
        internal string TemporaryPath { get; }
        internal DateTimeOffset StartedAtUtc { get; }

        internal static ActiveCapture Start(int processId, string captureId, string finalPath,
            string temporaryPath, DateTimeOffset startedAtUtc, Func<SafetyCheck> safetyGuard,
            Action<ActiveCapture, string> automaticAbort)
        {
            var capture = new ActiveCapture(processId, captureId, finalPath, temporaryPath,
                startedAtUtc, safetyGuard, automaticAbort);
            try
            {
                capture.InitializeAsync().GetAwaiter().GetResult();
                return capture;
            }
            catch
            {
                capture.Abort("activation_failed");
                throw;
            }
        }

        private async Task InitializeAsync()
        {
            audioClient = await ProcessLoopbackInterop.ActivateAsync(ProcessId, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
            var format = new WaveFormatEx
            {
                FormatTag = 1,
                Channels = Channels,
                SamplesPerSec = SampleRate,
                AvgBytesPerSec = AverageBytesPerSecond,
                BlockAlign = BlockAlign,
                BitsPerSample = BitsPerSample,
                ExtraSize = 0
            };
            var formatPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
            try
            {
                Marshal.StructureToPtr(format, formatPointer, fDeleteOld: false);
                Marshal.ThrowExceptionForHR(audioClient.Initialize(
                    0,
                    AudioClientStreamFlagsLoopback | AudioClientStreamFlagsEventCallback | AudioClientStreamFlagsAutoConvertPcm,
                    0,
                    0,
                    formatPointer,
                    nint.Zero));
            }
            finally { Marshal.FreeCoTaskMem(formatPointer); }

            Marshal.ThrowExceptionForHR(audioClient.GetService(AudioCaptureClientGuid, out var capturePointer));
            try { captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(capturePointer); }
            finally { if (capturePointer != nint.Zero) Marshal.Release(capturePointer); }
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(sampleReady.SafeWaitHandle.DangerousGetHandle()));
            Marshal.ThrowExceptionForHR(audioClient.Start());
            pump = Task.Run(PumpAsync);
        }

        private async Task PumpAsync()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    var safety = safetyGuard();
                    if (!safety.Safe)
                    {
                        abortReason = $"{safety.Code}: {safety.Message}";
                        break;
                    }
                    if (!sampleReady.WaitOne(50)) continue;
                    DrainPackets();
                    await Task.Yield();
                }
            }
            catch (Exception ex)
            {
                abortReason = $"audio_pump_failed: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                try { if (audioClient is not null) Marshal.ThrowExceptionForHR(audioClient.Stop()); }
                catch (Exception ex) { abortReason ??= $"audio_stop_failed: {ex.Message}"; }

                if (abortReason is not null)
                {
                    CloseAndDelete();
                    automaticAbort(this, abortReason);
                }
            }
        }

        private void DrainPackets()
        {
            var client = captureClient ?? throw new InvalidOperationException("Audio capture client is unavailable.");
            while (true)
            {
                Marshal.ThrowExceptionForHR(client.GetNextPacketSize(out var packetFrames));
                if (packetFrames == 0) return;
                Marshal.ThrowExceptionForHR(client.GetBuffer(out var data, out var frames,
                    out var flags, out _, out _));
                try
                {
                    var byteCount = checked((int)(frames * BlockAlign));
                    if (dataBytes + byteCount > uint.MaxValue)
                        throw new InvalidOperationException("WAV capture exceeded the RIFF 4 GiB size limit.");
                    if ((flags & AudioClientBufferFlagsSilent) != 0)
                        writer.Write(new byte[byteCount]);
                    else
                    {
                        var buffer = new byte[byteCount];
                        Marshal.Copy(data, buffer, 0, byteCount);
                        writer.Write(buffer);
                    }
                    dataBytes += byteCount;
                }
                finally { Marshal.ThrowExceptionForHR(client.ReleaseBuffer(frames)); }
            }
        }

        internal AudioCaptureArtifact Complete(AudioCaptureCapability capability)
        {
            lock (sync)
            {
                if (disposed) throw new InvalidOperationException(abortReason ?? "Audio capture is no longer active.");
                cancellation.Cancel();
            }
            pump?.GetAwaiter().GetResult();
            lock (sync)
            {
                if (abortReason is not null)
                    throw new InvalidOperationException($"Audio capture aborted: {abortReason}");
                writer.Flush();
                output.Flush(flushToDisk: true);
                output.Position = 0;
                WriteWaveHeader(writer, checked((uint)dataBytes));
                writer.Flush();
                output.Flush(flushToDisk: true);
                DisposeResources();
                File.Move(TemporaryPath, FinalPath, overwrite: false);
                var completedAt = DateTimeOffset.UtcNow;
                return new(CaptureId, FinalPath, "audio/wav", new FileInfo(FinalPath).Length,
                    TimeSpan.FromSeconds((double)dataBytes / AverageBytesPerSecond), SampleRate,
                    Channels, BitsPerSample, StartedAtUtc, completedAt,
                    EvidenceFile.ComputeSha256(FinalPath), capability);
            }
        }

        internal void Abort(string reason)
        {
            lock (sync)
            {
                if (disposed) return;
                abortReason ??= reason;
                cancellation.Cancel();
            }
            try { pump?.GetAwaiter().GetResult(); }
            catch { /* Abort must remain best effort and non-throwing. */ }
            CloseAndDelete();
        }

        private void CloseAndDelete()
        {
            lock (sync)
            {
                DisposeResources();
                TryDelete(TemporaryPath);
            }
        }

        private void DisposeResources()
        {
            if (disposed) return;
            disposed = true;
            writer.Dispose();
            output.Dispose();
            sampleReady.Dispose();
            cancellation.Dispose();
            ReleaseCom(captureClient);
            ReleaseCom(audioClient);
            captureClient = null;
            audioClient = null;
        }

        private static void ReleaseCom(object? value)
        {
            if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }

        private static void WriteWaveHeader(BinaryWriter destination, uint dataSize)
        {
            destination.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            destination.Write(checked(36u + dataSize));
            destination.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            destination.Write(16u);
            destination.Write((ushort)1);
            destination.Write((ushort)Channels);
            destination.Write((uint)SampleRate);
            destination.Write((uint)AverageBytesPerSecond);
            destination.Write((ushort)BlockAlign);
            destination.Write((ushort)BitsPerSample);
            destination.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            destination.Write(dataSize);
        }
    }
}

public sealed class UnsupportedAudioCaptureBackend : IAudioCaptureBackend
{
    public AudioCaptureCapability Capability { get; } = new(
        "disabled", CaptureAvailability.Unsupported, false, false, [], 20348,
        "Audio capture was explicitly disabled by configuration.");
    public AudioCaptureState State => new(null, AudioCaptureStatus.Idle, null, null, null, null, Capability);
    public AudioCaptureState Start(ProcessIdentity identity, string destinationPath, Func<SafetyCheck> safetyGuard) =>
        throw new NotSupportedException(Capability.Limitation);
    public AudioCaptureArtifact Stop(string captureId) => throw new NotSupportedException(Capability.Limitation);
    public void Abort(string reason) { }
    public void Dispose() { }
}

internal static class ProcessLoopbackInterop
{
    private const ushort VariantBlob = 65;
    private const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";
    private static readonly Guid AudioClientGuid = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    internal static async Task<IAudioClient> ActivateAsync(int processId, TimeSpan timeout)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1,
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams
            {
                TargetProcessId = checked((uint)processId),
                ProcessLoopbackMode = 0
            }
        };
        var activationPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<AudioClientActivationParams>());
        try
        {
            Marshal.StructureToPtr(activation, activationPointer, fDeleteOld: false);
            var parameters = new PropVariant
            {
                VariantType = VariantBlob,
                Blob = new Blob
                {
                    Size = checked((uint)Marshal.SizeOf<AudioClientActivationParams>()),
                    Data = activationPointer
                }
            };
            var completion = new ActivationCompletionHandler();
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback,
                AudioClientGuid,
                parameters,
                completion,
                out var operation));
            try
            {
                nint interfacePointer;
                try { interfacePointer = await completion.TakeAsync(timeout).ConfigureAwait(false); }
                catch
                {
                    completion.Cancel();
                    throw;
                }
                try { return (IAudioClient)Marshal.GetObjectForIUnknown(interfacePointer); }
                finally { Marshal.Release(interfacePointer); }
            }
            finally { ReleaseCom(operation); }
        }
        finally { Marshal.FreeCoTaskMem(activationPointer); }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly object sync = new();
        private readonly TaskCompletionSource<nint> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private nint pointer;
        private bool canceled;
        private bool taken;

        internal async Task<nint> TakeAsync(TimeSpan timeout)
        {
            var result = await completion.Task.WaitAsync(timeout).ConfigureAwait(false);
            lock (sync)
            {
                if (canceled) throw new OperationCanceledException("Audio activation was canceled.");
                taken = true;
                pointer = nint.Zero;
                return result;
            }
        }

        internal void Cancel()
        {
            nint release = nint.Zero;
            lock (sync)
            {
                canceled = true;
                if (!taken && pointer != nint.Zero)
                {
                    release = pointer;
                    pointer = nint.Zero;
                }
            }
            if (release != nint.Zero) Marshal.Release(release);
        }

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            var activatedInterface = nint.Zero;
            try
            {
                Marshal.ThrowExceptionForHR(operation.GetActivateResult(out var activationResult, out activatedInterface));
                Marshal.ThrowExceptionForHR(activationResult);
                lock (sync)
                {
                    if (canceled)
                    {
                        Marshal.Release(activatedInterface);
                        return 0;
                    }
                    pointer = activatedInterface;
                }
                if (!completion.TrySetResult(activatedInterface))
                {
                    lock (sync) pointer = nint.Zero;
                    Marshal.Release(activatedInterface);
                }
            }
            catch (Exception ex)
            {
                if (activatedInterface != nint.Zero) Marshal.Release(activatedInterface);
                completion.TrySetException(ex);
            }
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid iid,
        in PropVariant activationParams,
        [MarshalAs(UnmanagedType.Interface)] IActivateAudioInterfaceCompletionHandler completionHandler,
        [MarshalAs(UnmanagedType.Interface)] out IActivateAudioInterfaceAsyncOperation operation);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WaveFormatEx
{
    internal ushort FormatTag;
    internal short Channels;
    internal int SamplesPerSec;
    internal int AvgBytesPerSec;
    internal short BlockAlign;
    internal short BitsPerSample;
    internal short ExtraSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientProcessLoopbackParams
{
    internal uint TargetProcessId;
    internal int ProcessLoopbackMode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AudioClientActivationParams
{
    internal int ActivationType;
    internal AudioClientProcessLoopbackParams ProcessLoopbackParams;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Blob
{
    internal uint Size;
    internal nint Data;
}

[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    [FieldOffset(0)] internal ushort VariantType;
    [FieldOffset(8)] internal Blob Blob;
}

[ComImport]
[Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig]
    int GetActivateResult(out int activateResult, out nint activatedInterface);
}

[ComImport]
[Guid("41D949AB-9862-444A-80F6-C261334DA5EB")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig]
    int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
}

[ComImport]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration,
        long periodicity, nint format, nint audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint bufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint deviceFormat);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint eventHandle);
    [PreserveSig] int GetService(in Guid iid, out nint service);
}

[ComImport]
[Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out nint data, out uint frames, out uint flags,
        out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}
