using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Windows;

public enum RollingVideoCaptureStatus
{
    Completed,
    Aborted,
    AbortedSafety,
    Failed
}

public sealed record RollingVideoCaptureOptions(
    string BundleDirectory,
    int FramesPerSecond,
    int RingSeconds,
    string? FinalMp4Path = null)
{
    public int MaximumFrames => checked(FramesPerSecond * RingSeconds);

    public void Validate()
    {
        if (FramesPerSecond is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(FramesPerSecond), "Frame rate must be between 1 and 10 FPS.");
        if (RingSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(RingSeconds), "Ring duration must be between 1 and 300 seconds.");
        if (string.IsNullOrWhiteSpace(BundleDirectory) || !Path.IsPathFullyQualified(BundleDirectory))
            throw new ArgumentException("Bundle directory must be an absolute path.", nameof(BundleDirectory));
        if (FinalMp4Path is not null &&
            (string.IsNullOrWhiteSpace(FinalMp4Path) || !Path.IsPathFullyQualified(FinalMp4Path)))
            throw new ArgumentException("Final MP4 path must be an absolute path when supplied.", nameof(FinalMp4Path));
    }
}

public sealed record RollingVideoFrameEvidence(
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    string FileName,
    int Width,
    int Height,
    long Bytes,
    string Sha256,
    string PreviousSha256,
    string ChainSha256);

public sealed record RollingVideoManifest(
    string Schema,
    RollingVideoCaptureStatus Status,
    string StopCode,
    int ProcessId,
    DateTimeOffset ProcessCreationTimeUtc,
    string ExecutableSha256,
    long WindowHandle,
    int FramesPerSecond,
    int RingSeconds,
    int MaximumFrames,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string ChainAnchorSha256,
    string ChainRootSha256,
    IReadOnlyList<RollingVideoFrameEvidence> Frames);

public sealed record RollingVideoEncodingResult(
    bool Requested,
    bool Succeeded,
    string Code,
    string? Path,
    string? Sha256,
    string Message);

public sealed record RollingVideoCaptureResult(
    RollingVideoCaptureStatus Status,
    string Code,
    string BundleDirectory,
    string ManifestPath,
    string ManifestSha256,
    RollingVideoManifest Manifest,
    RollingVideoEncodingResult Encoding);

public interface IRollingWindowFrameSource
{
    ValueTask<CaptureArtifact> CaptureAsync(
        ProcessIdentity identity,
        string destinationPath,
        CancellationToken cancellationToken);
}

public interface IRollingCaptureGuard
{
    SafetyCheck Validate(ProcessIdentity identity);
}

public interface IRollingCaptureClock
{
    DateTimeOffset GetUtcNow();
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public interface IRollingVideoEncoder
{
    Task EncodeAsync(RollingVideoEncodingRequest request, CancellationToken cancellationToken);
}

public sealed record RollingVideoEncodingRequest(
    string FramePattern,
    long FirstSequence,
    int FrameCount,
    int FramesPerSecond,
    string DestinationPath);

public sealed class WindowsGraphicsCaptureFrameSource : IRollingWindowFrameSource
{
    private readonly WindowsGraphicsCaptureBackend backend = new();

    public async ValueTask<CaptureArtifact> CaptureAsync(
        ProcessIdentity identity,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This deliberately has no GDI, monitor, desktop, or system-capture fallback.
        var artifact = await Task.Run(
            () => backend.Capture(identity, destinationPath),
            CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return artifact;
    }
}

public sealed class LeasedWindowRollingCaptureGuard : IRollingCaptureGuard
{
    private readonly ClientLeaseManager leases;
    private readonly string leaseId;
    private readonly ProcessIdentity pinnedIdentity;
    private readonly Func<SafetyCheck> sessionSafety;

    public LeasedWindowRollingCaptureGuard(
        ClientLeaseManager leases,
        string leaseId,
        ProcessIdentity pinnedIdentity,
        Func<SafetyCheck> sessionSafety)
    {
        this.leases = leases ?? throw new ArgumentNullException(nameof(leases));
        this.leaseId = string.IsNullOrWhiteSpace(leaseId)
            ? throw new ArgumentException("Lease id is required.", nameof(leaseId))
            : leaseId;
        this.pinnedIdentity = pinnedIdentity;
        this.sessionSafety = sessionSafety ?? throw new ArgumentNullException(nameof(sessionSafety));
    }

    public SafetyCheck Validate(ProcessIdentity identity)
    {
        if (identity != pinnedIdentity)
            return SafetyCheck.Fail("capture.identity_drift", "Capture identity differs from the pinned window identity.");
        var lease = leases.Revalidate(leaseId, requireForeground: false);
        if (!lease.Safe) return lease;
        var safety = sessionSafety();
        if (!safety.Safe) return safety;
        var window = (nint)identity.WindowHandle;
        if (window == nint.Zero || !NativeMethods.IsWindow(window))
            return SafetyCheck.Fail("capture.window_invalid", "The pinned HWND is no longer valid.");
        _ = NativeMethods.GetWindowThreadProcessId(window, out var owner);
        if (owner != identity.ProcessId)
            return SafetyCheck.Fail("capture.window_owner", "The pinned HWND owner changed.");
        if (NativeMethods.IsIconic(window))
            return SafetyCheck.Fail("capture.window_minimized", "Rolling capture rejects minimized windows.");
        return SafetyCheck.Pass();
    }
}

public sealed class SystemRollingCaptureClock : IRollingCaptureClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}

public sealed class RollingWindowCaptureService
{
    private const string ManifestFileName = "manifest.json";
    private const string ManifestHashFileName = "manifest.sha256";
    private static readonly string ZeroSha256 = new('0', 64);
    private readonly IRollingWindowFrameSource frames;
    private readonly IRollingCaptureGuard guard;
    private readonly IRollingCaptureClock clock;
    private readonly IRollingVideoEncoder? encoder;
    private int activeCapture;

    public RollingWindowCaptureService(
        IRollingWindowFrameSource frames,
        IRollingCaptureGuard guard,
        IRollingCaptureClock? clock = null,
        IRollingVideoEncoder? encoder = null)
    {
        this.frames = frames ?? throw new ArgumentNullException(nameof(frames));
        this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
        this.clock = clock ?? new SystemRollingCaptureClock();
        this.encoder = encoder;
    }

    public RollingWindowCaptureService(
        ClientLeaseManager leases,
        string leaseId,
        ProcessIdentity pinnedIdentity,
        Func<SafetyCheck> sessionSafety,
        IRollingCaptureClock? clock = null,
        IRollingVideoEncoder? encoder = null)
        : this(
            new WindowsGraphicsCaptureFrameSource(),
            new LeasedWindowRollingCaptureGuard(leases, leaseId, pinnedIdentity, sessionSafety),
            clock,
            encoder)
    {
    }

    public Task<RollingVideoCaptureResult> CaptureAsync(
        ProcessIdentity identity,
        RollingVideoCaptureOptions options,
        CancellationToken stopToken) =>
        CaptureAsync(identity, options, stopToken, CancellationToken.None);

    public async Task<RollingVideoCaptureResult> CaptureAsync(
        ProcessIdentity identity,
        RollingVideoCaptureOptions options,
        CancellationToken stopToken,
        CancellationToken abortToken)
    {
        if (Interlocked.CompareExchange(ref activeCapture, 1, 0) != 0)
            throw new InvalidOperationException("This rolling capture service already has an active window capture.");
        try
        {
            return await CaptureCoreAsync(identity, options, stopToken, abortToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref activeCapture, 0);
        }
    }

    private async Task<RollingVideoCaptureResult> CaptureCoreAsync(
        ProcessIdentity identity,
        RollingVideoCaptureOptions options,
        CancellationToken stopToken,
        CancellationToken abortToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        ValidateIdentity(identity);

        var bundle = Path.GetFullPath(options.BundleDirectory);
        if (Directory.Exists(bundle) || File.Exists(bundle))
            throw new IOException("Rolling evidence bundle destination already exists.");
        if (options.FinalMp4Path is { } mp4 && (Directory.Exists(mp4) || File.Exists(mp4)))
            throw new IOException("Final MP4 destination already exists.");

        var parent = Path.GetDirectoryName(bundle)
            ?? throw new InvalidOperationException("Bundle directory has no parent.");
        Directory.CreateDirectory(parent);
        var work = Path.Combine(parent, $".{Path.GetFileName(bundle)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(work);

        var retained = new Queue<RollingVideoFrameEvidence>();
        var started = clock.GetUtcNow();
        var sequence = 0L;
        var chainRoot = ZeroSha256;
        var chainAnchor = ZeroSha256;
        var status = RollingVideoCaptureStatus.Completed;
        var stopCode = "capture-stopped";
        string? activeFrameTemp = null;
        string? activeFrameFinal = null;
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken, abortToken);
        var captureToken = captureCancellation.Token;

        try
        {
            while (true)
            {
                captureToken.ThrowIfCancellationRequested();
                AssertSafe(identity);

                if (retained.Count == options.MaximumFrames)
                {
                    var removed = retained.Dequeue();
                    DeleteRequired(Path.Combine(work, removed.FileName));
                    chainAnchor = removed.ChainSha256;
                }

                sequence++;
                var fileName = $"frame-{sequence:D10}.bmp";
                var finalFrame = Path.Combine(work, fileName);
                activeFrameTemp = Path.Combine(work, $".{fileName}.{Guid.NewGuid():N}.tmp");
                var artifact = await frames.CaptureAsync(identity, activeFrameTemp, captureToken).ConfigureAwait(false);
                AssertSafe(identity);
                ValidateFrameArtifact(artifact, activeFrameTemp);
                File.Move(activeFrameTemp, finalFrame, overwrite: false);
                activeFrameTemp = null;
                activeFrameFinal = finalFrame;

                var capturedAt = clock.GetUtcNow();
                var bytes = new FileInfo(finalFrame).Length;
                var sha256 = ComputeSha256(finalFrame);
                var previous = chainRoot;
                var chainHash = ComputeFrameChainHash(sequence, capturedAt, fileName,
                    artifact.Width, artifact.Height, bytes, sha256, previous);
                chainRoot = chainHash;
                retained.Enqueue(new(sequence, capturedAt, fileName, artifact.Width, artifact.Height,
                    bytes, sha256, previous, chainHash));
                activeFrameFinal = null;

                await clock.DelayAsync(
                    TimeSpan.FromSeconds(1d / options.FramesPerSecond),
                    captureToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (abortToken.IsCancellationRequested)
        {
            status = RollingVideoCaptureStatus.Aborted;
            stopCode = "capture-aborted";
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            status = RollingVideoCaptureStatus.Completed;
            stopCode = "capture-stopped";
        }
        catch (RollingCaptureSafetyException failure)
        {
            status = RollingVideoCaptureStatus.AbortedSafety;
            stopCode = failure.Code;
        }
        catch (Exception failure)
        {
            status = RollingVideoCaptureStatus.Failed;
            stopCode = $"capture-failed:{failure.GetType().Name}";
        }
        finally
        {
            TryDelete(activeFrameTemp);
            TryDelete(activeFrameFinal);
        }

        var manifest = new RollingVideoManifest(
            "hytale-qa/rolling-video/v1",
            status,
            stopCode,
            identity.ProcessId,
            identity.CreationTimeUtc,
            identity.Sha256.ToLowerInvariant(),
            identity.WindowHandle,
            options.FramesPerSecond,
            options.RingSeconds,
            options.MaximumFrames,
            started,
            clock.GetUtcNow(),
            chainAnchor,
            retained.Count == 0 ? chainAnchor : chainRoot,
            retained.ToArray());

        string manifestHash;
        try
        {
            var manifestBytes = RollingVideoEvidence.WriteCanonicalManifest(manifest);
            var manifestPath = Path.Combine(work, ManifestFileName);
            WriteAtomic(manifestPath, manifestBytes);
            manifestHash = ComputeSha256(manifestPath);
            WriteAtomic(Path.Combine(work, ManifestHashFileName), Encoding.ASCII.GetBytes($"{manifestHash}\n"));
            Directory.Move(work, bundle);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }

        var encoding = await EncodeIfRequestedAsync(bundle, options, manifest, CancellationToken.None)
            .ConfigureAwait(false);
        return new(status, stopCode, bundle, Path.Combine(bundle, ManifestFileName), manifestHash, manifest, encoding);
    }

    private async Task<RollingVideoEncodingResult> EncodeIfRequestedAsync(
        string bundle,
        RollingVideoCaptureOptions options,
        RollingVideoManifest manifest,
        CancellationToken cancellationToken)
    {
        if (options.FinalMp4Path is null)
            return new(false, false, "not-requested", null, null, "MP4 encoding was not requested.");
        if (manifest.Status != RollingVideoCaptureStatus.Completed)
            return new(true, false, "capture-not-complete", null, null,
                "MP4 encoding is skipped for failed or safety-aborted capture.");
        if (manifest.Frames.Count == 0)
            return new(true, false, "no-frames", null, null, "No retained frames are available to encode.");
        if (encoder is null)
            return new(true, false, "encoder-not-configured", null, null,
                "No allowlisted encoder was configured; the raw frame bundle was preserved.");

        var destination = Path.GetFullPath(options.FinalMp4Path);
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("MP4 destination has no parent.");
        Directory.CreateDirectory(destinationParent);
        var temporary = Path.Combine(destinationParent,
            $".{Path.GetFileNameWithoutExtension(destination)}.{Guid.NewGuid():N}.tmp.mp4");
        try
        {
            var firstSequence = manifest.Frames[0].Sequence;
            var request = new RollingVideoEncodingRequest(
                Path.Combine(bundle, "frame-%010d.bmp"),
                firstSequence,
                manifest.Frames.Count,
                options.FramesPerSecond,
                temporary);
            await encoder.EncodeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("Encoder did not produce a non-empty MP4.");
            File.Move(temporary, destination, overwrite: false);
            return new(true, true, "encoded", destination, ComputeSha256(destination), "MP4 encoding completed.");
        }
        catch (Exception failure)
        {
            TryDelete(temporary);
            return new(true, false, "encoding-failed", null, null,
                $"MP4 encoding failed; raw frames were preserved. {failure.Message}");
        }
    }

    private void AssertSafe(ProcessIdentity identity)
    {
        SafetyCheck check;
        try
        {
            check = guard.Validate(identity);
        }
        catch (Exception failure)
        {
            throw new RollingCaptureSafetyException(
                "capture.safety_unavailable",
                "Capture safety validation failed.",
                failure);
        }
        if (!check.Safe) throw new RollingCaptureSafetyException(check.Code, check.Message);
    }

    private static void ValidateIdentity(ProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || identity.WindowHandle == 0)
            throw new ArgumentException("A concrete process and HWND are required for window-only capture.", nameof(identity));
        if (identity.Sha256.Length != 64 || !identity.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Pinned executable SHA-256 is invalid.", nameof(identity));
    }

    private static void ValidateFrameArtifact(CaptureArtifact artifact, string expectedPath)
    {
        if (!PathEquals(artifact.Path, expectedPath))
            throw new InvalidDataException("Frame source wrote outside the assigned window-frame path.");
        if (!string.Equals(artifact.MediaType, "image/bmp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rolling evidence accepts BMP window frames only.");
        if (artifact.Width <= 0 || artifact.Height <= 0)
            throw new InvalidDataException("Captured frame dimensions are invalid.");
        if (!File.Exists(expectedPath)) throw new FileNotFoundException("Captured frame is missing.", expectedPath);
        using var stream = File.OpenRead(expectedPath);
        if (stream.Length < 2 || stream.ReadByte() != 'B' || stream.ReadByte() != 'M')
            throw new InvalidDataException("Captured frame is not a BMP file.");
    }

    private static string ComputeFrameChainHash(
        long sequence,
        DateTimeOffset capturedAt,
        string fileName,
        int width,
        int height,
        long bytes,
        string sha256,
        string previousSha256)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("capturedAtUtc", capturedAt);
            writer.WriteString("fileName", fileName);
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteNumber("bytes", bytes);
            writer.WriteString("sha256", sha256);
            writer.WriteString("previousSha256", previousSha256);
            writer.WriteEndObject();
        }
        return ComputeSha256(stream.ToArray());
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Atomic evidence path has no parent.");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void DeleteRequired(string path)
    {
        File.Delete(path);
        if (File.Exists(path)) throw new IOException("Unable to evict an expired rolling frame.");
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary capture/encoding result.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Preserve the primary finalization failure.
        }
    }
}

public static class RollingVideoEvidence
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static byte[] WriteCanonicalManifest(RollingVideoManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", manifest.Schema);
            writer.WriteString("status", manifest.Status.ToString());
            writer.WriteString("stopCode", manifest.StopCode);
            writer.WriteNumber("processId", manifest.ProcessId);
            writer.WriteString("processCreationTimeUtc", manifest.ProcessCreationTimeUtc);
            writer.WriteString("executableSha256", manifest.ExecutableSha256);
            writer.WriteNumber("windowHandle", manifest.WindowHandle);
            writer.WriteNumber("framesPerSecond", manifest.FramesPerSecond);
            writer.WriteNumber("ringSeconds", manifest.RingSeconds);
            writer.WriteNumber("maximumFrames", manifest.MaximumFrames);
            writer.WriteString("startedAtUtc", manifest.StartedAtUtc);
            writer.WriteString("finishedAtUtc", manifest.FinishedAtUtc);
            writer.WriteString("chainAnchorSha256", manifest.ChainAnchorSha256);
            writer.WriteString("chainRootSha256", manifest.ChainRootSha256);
            writer.WritePropertyName("frames");
            writer.WriteStartArray();
            foreach (var frame in manifest.Frames)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", frame.Sequence);
                writer.WriteString("capturedAtUtc", frame.CapturedAtUtc);
                writer.WriteString("fileName", frame.FileName);
                writer.WriteNumber("width", frame.Width);
                writer.WriteNumber("height", frame.Height);
                writer.WriteNumber("bytes", frame.Bytes);
                writer.WriteString("sha256", frame.Sha256);
                writer.WriteString("previousSha256", frame.PreviousSha256);
                writer.WriteString("chainSha256", frame.ChainSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static bool VerifyFrameChain(RollingVideoManifest manifest, out string rootSha256)
    {
        var previous = manifest.ChainAnchorSha256;
        foreach (var frame in manifest.Frames)
        {
            if (!IsSha256(frame.Sha256) || !IsSha256(frame.PreviousSha256) || !IsSha256(frame.ChainSha256) ||
                !FixedTimeTextEquals(previous, frame.PreviousSha256))
            {
                rootSha256 = previous;
                return false;
            }
            var actual = ComputeFrameHash(frame);
            if (!FixedTimeTextEquals(actual, frame.ChainSha256))
            {
                rootSha256 = previous;
                return false;
            }
            previous = actual;
        }
        rootSha256 = previous;
        return FixedTimeTextEquals(previous, manifest.ChainRootSha256);
    }

    public static bool VerifyBundle(string bundleDirectory, out string code)
    {
        try
        {
            var bundle = Path.GetFullPath(bundleDirectory);
            var manifestPath = Path.Combine(bundle, "manifest.json");
            var manifestHashPath = Path.Combine(bundle, "manifest.sha256");
            if (!Directory.Exists(bundle) || !File.Exists(manifestPath) || !File.Exists(manifestHashPath))
            {
                code = "bundle.incomplete";
                return false;
            }

            var manifestBytes = File.ReadAllBytes(manifestPath);
            var recordedManifestHash = File.ReadAllText(manifestHashPath).Trim();
            var actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
            if (!IsSha256(recordedManifestHash) || !FixedTimeTextEquals(recordedManifestHash, actualManifestHash))
            {
                code = "manifest.hash";
                return false;
            }

            var manifest = JsonSerializer.Deserialize<RollingVideoManifest>(manifestBytes, ManifestJsonOptions);
            if (manifest is null || !manifestBytes.AsSpan().SequenceEqual(WriteCanonicalManifest(manifest)))
            {
                code = "manifest.canonical";
                return false;
            }
            if (manifest.Schema != "hytale-qa/rolling-video/v1" ||
                manifest.FramesPerSecond is < 1 or > 10 ||
                manifest.RingSeconds is < 1 or > 300 ||
                manifest.MaximumFrames != checked(manifest.FramesPerSecond * manifest.RingSeconds) ||
                manifest.Frames.Count > manifest.MaximumFrames ||
                !IsSha256(manifest.ExecutableSha256) ||
                !IsSha256(manifest.ChainAnchorSha256) ||
                !IsSha256(manifest.ChainRootSha256))
            {
                code = "manifest.contract";
                return false;
            }
            if (!VerifyFrameChain(manifest, out _))
            {
                code = "frames.chain";
                return false;
            }
            if (Directory.EnumerateFileSystemEntries(bundle, "*.tmp", SearchOption.AllDirectories).Any() ||
                Directory.EnumerateFileSystemEntries(bundle, "*.tmp.mp4", SearchOption.AllDirectories).Any())
            {
                code = "bundle.temporary";
                return false;
            }

            long? previousSequence = null;
            var expectedFrames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var frame in manifest.Frames)
            {
                if (frame.FileName != $"frame-{frame.Sequence:D10}.bmp" ||
                    Path.GetFileName(frame.FileName) != frame.FileName ||
                    previousSequence is { } previous && frame.Sequence != previous + 1)
                {
                    code = "frame.identity";
                    return false;
                }
                var path = Path.Combine(bundle, frame.FileName);
                if (!File.Exists(path) || new FileInfo(path).Length != frame.Bytes)
                {
                    code = "frame.file";
                    return false;
                }
                using var input = File.OpenRead(path);
                var actual = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
                if (!FixedTimeTextEquals(actual, frame.Sha256))
                {
                    code = "frame.hash";
                    return false;
                }
                expectedFrames.Add(frame.FileName);
                previousSequence = frame.Sequence;
            }
            if (!expectedFrames.SetEquals(Directory.EnumerateFiles(bundle, "*.bmp", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileName)!))
            {
                code = "frame.set";
                return false;
            }

            code = "verified";
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or
                                        JsonException or ArgumentException or OverflowException)
        {
            code = "bundle.unreadable";
            return false;
        }
    }

    private static string ComputeFrameHash(RollingVideoFrameEvidence frame)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", frame.Sequence);
            writer.WriteString("capturedAtUtc", frame.CapturedAtUtc);
            writer.WriteString("fileName", frame.FileName);
            writer.WriteNumber("width", frame.Width);
            writer.WriteNumber("height", frame.Height);
            writer.WriteNumber("bytes", frame.Bytes);
            writer.WriteString("sha256", frame.Sha256);
            writer.WriteString("previousSha256", frame.PreviousSha256);
            writer.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private static bool FixedTimeTextEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToLowerInvariant()),
            Encoding.ASCII.GetBytes(right.ToLowerInvariant()));
}

public sealed record FfmpegAllowlist(string ExecutablePath, string Sha256);

public sealed class AllowlistedFfmpegEncoder : IRollingVideoEncoder
{
    private readonly string executablePath;
    private readonly string expectedSha256;
    private readonly TimeSpan executionTimeout;

    public AllowlistedFfmpegEncoder(FfmpegAllowlist allowlist, TimeSpan? executionTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(allowlist);
        if (string.IsNullOrWhiteSpace(allowlist.ExecutablePath) ||
            !Path.IsPathFullyQualified(allowlist.ExecutablePath) ||
            allowlist.ExecutablePath.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("FFmpeg must be an exact absolute local path.", nameof(allowlist));
        if (allowlist.Sha256.Length != 64 || !allowlist.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("FFmpeg SHA-256 allowlist value is invalid.", nameof(allowlist));
        executablePath = Path.GetFullPath(allowlist.ExecutablePath);
        expectedSha256 = allowlist.Sha256.ToLowerInvariant();
        this.executionTimeout = executionTimeout ?? TimeSpan.FromMinutes(5);
        if (this.executionTimeout < TimeSpan.FromSeconds(1) ||
            this.executionTimeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(executionTimeout),
                "FFmpeg execution timeout must be between one second and ten minutes.");
    }

    public async Task EncodeAsync(RollingVideoEncodingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Allowlisted FFmpeg was not found.", executablePath);
        if ((File.GetAttributes(executablePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Allowlisted FFmpeg path cannot be a reparse point.");
        if (!FixedTimeHashEquals(ComputeSha256(executablePath), expectedSha256))
            throw new InvalidOperationException("FFmpeg SHA-256 does not match the exact allowlist.");
        if (request.FrameCount <= 0 || request.FramesPerSecond is < 1 or > 10 || request.FirstSequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Encoding request contains invalid frame bounds.");
        if (!Path.IsPathFullyQualified(request.FramePattern) || !Path.IsPathFullyQualified(request.DestinationPath))
            throw new ArgumentException("Encoding paths must be absolute.", nameof(request));

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        string[] arguments =
        [
            "-hide_banner", "-loglevel", "error", "-nostdin", "-n",
            "-framerate", request.FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-start_number", request.FirstSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", request.FramePattern,
            "-frames:v", request.FrameCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-pix_fmt", "yuv420p", request.DestinationPath
        ];
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start allowlisted FFmpeg.");
        using var timeout = new CancellationTokenSource(executionTimeout);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(execution.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(execution.Token);
        try
        {
            await process.WaitForExitAsync(execution.Token).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"FFmpeg exited with code {process.ExitCode}: {Limit(stderr, 2048)}");
        }
        catch (OperationCanceledException failure) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await ObserveAsync(stderrTask, stdoutTask).ConfigureAwait(false);
            throw new TimeoutException("Allowlisted FFmpeg exceeded its execution timeout.", failure);
        }
        catch
        {
            Kill(process);
            await ObserveAsync(stderrTask, stdoutTask).ConfigureAwait(false);
            throw;
        }
        if (!FixedTimeHashEquals(ComputeSha256(executablePath), expectedSha256))
            throw new InvalidOperationException("FFmpeg changed while encoding.");
    }

    private static string ComputeSha256(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static bool FixedTimeHashEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static void Kill(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch { /* Preserve the original encoder failure. */ }
    }

    private static async Task ObserveAsync(params Task<string>[] reads)
    {
        try { await Task.WhenAll(reads).ConfigureAwait(false); }
        catch { /* Read cancellation is secondary to the encoder failure. */ }
    }

    private static string Limit(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}

public sealed class RollingCaptureSafetyException : Exception
{
    public RollingCaptureSafetyException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;

    public string Code { get; }
}
