using Hytale.Qa.Contracts;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class MediaCaptureTests
{
    [Fact]
    public void WindowsGraphicsCaptureCapabilityMatchesRuntimeProbe()
    {
        var backend = new WindowsGraphicsCaptureBackend();

        Assert.Equal(WindowsGraphicsCaptureBackend.IsSupported(),
            backend.Capability.Availability == CaptureAvailability.Available);
        Assert.True(backend.Capability.WorksWhenOccluded);
        Assert.False(backend.Capability.WorksWhenMinimized);
        Assert.Contains("image/bmp", backend.Capability.Formats);
    }

    [Fact]
    public void ProcessLoopbackRejectsUnsafeSessionBeforeCreatingFile()
    {
        using var backend = new ProcessLoopbackAudioCaptureBackend();
        var destination = Path.Combine(Path.GetTempPath(), $"hytale-qa-audio-{Guid.NewGuid():N}.wav");
        var identity = new ProcessIdentity(Environment.ProcessId, DateTimeOffset.UtcNow,
            "test.exe", new string('A', 64), 1);

        var error = Assert.Throws<InvalidOperationException>(() => backend.Start(identity, destination,
            () => SafetyCheck.Fail("proof.stale", "test proof is stale")));

        Assert.Contains("proof.stale", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(destination));
        Assert.Equal(AudioCaptureStatus.Idle, backend.State.Status);
    }

    [Fact]
    public void UnsupportedAudioBackendNeverFallsBackToSystemLoopback()
    {
        using var backend = new UnsupportedAudioCaptureBackend();
        var identity = new ProcessIdentity(Environment.ProcessId, DateTimeOffset.UtcNow,
            "test.exe", new string('A', 64), 1);

        Assert.False(backend.Capability.ProcessIsolated);
        Assert.Equal(CaptureAvailability.Unsupported, backend.Capability.Availability);
        Assert.Throws<NotSupportedException>(() => backend.Start(identity, "evidence.wav", () => SafetyCheck.Pass()));
    }

    [Fact]
    public async Task ProcessLoopbackProducesFinalizedWaveWhenOptedIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HYTALE_QA_RUN_WINDOWS_MEDIA_INTEGRATION"),
                "1", StringComparison.Ordinal)) return;

        using var backend = new ProcessLoopbackAudioCaptureBackend();
        if (backend.Capability.Availability != CaptureAvailability.Available) return;
        var destination = Path.Combine(Path.GetTempPath(), $"hytale-qa-audio-{Guid.NewGuid():N}.wav");
        var identity = new ProcessIdentity(Environment.ProcessId, DateTimeOffset.UtcNow,
            Environment.ProcessPath ?? "testhost.exe", new string('A', 64), 1);
        try
        {
            var state = backend.Start(identity, destination, () => SafetyCheck.Pass());
            await Task.Delay(250);
            var artifact = backend.Stop(state.CaptureId!);

            var bytes = await File.ReadAllBytesAsync(destination);
            Assert.True(bytes.Length >= 44);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            Assert.Equal(AudioCaptureStatus.Completed, backend.State.Status);
            Assert.Equal(bytes.LongLength, artifact.Bytes);
            Assert.Equal(64, artifact.Sha256.Length);
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task WindowsGraphicsCaptureProducesBitmapWhenOptedIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("HYTALE_QA_RUN_WINDOWS_MEDIA_INTEGRATION"),
                "1", StringComparison.Ordinal)) return;
        if (!WindowsGraphicsCaptureBackend.IsSupported()) return;

        var windowReady = new TaskCompletionSource<System.Windows.Forms.Form>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var windowThread = new Thread(() =>
        {
            using var form = new System.Windows.Forms.Form
            {
                Text = "Hytale QA Windows.Graphics.Capture probe",
                ClientSize = new System.Drawing.Size(320, 180),
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Location = new System.Drawing.Point(20, 20),
                ShowInTaskbar = false,
                BackColor = System.Drawing.Color.DarkSlateBlue
            };
            form.Shown += (_, _) => windowReady.TrySetResult(form);
            System.Windows.Forms.Application.Run(form);
        });
        windowThread.SetApartmentState(ApartmentState.STA);
        windowThread.Start();

        var form = await windowReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var destination = Path.Combine(Path.GetTempPath(), $"hytale-qa-wgc-{Guid.NewGuid():N}.bmp");
        try
        {
            var identity = new ProcessIdentity(Environment.ProcessId, DateTimeOffset.UtcNow,
                Environment.ProcessPath ?? "testhost.exe", new string('A', 64), form.Handle);
            var artifact = new WindowsGraphicsCaptureBackend().Capture(identity, destination);
            var bytes = await File.ReadAllBytesAsync(destination);

            Assert.True(bytes.Length > 54);
            Assert.Equal((byte)'B', bytes[0]);
            Assert.Equal((byte)'M', bytes[1]);
            Assert.Equal(320, artifact.Width);
            Assert.Equal(180, artifact.Height);
            Assert.Equal("windowed", artifact.DisplayMode);
            Assert.Equal(64, artifact.Sha256.Length);
        }
        finally
        {
            form.BeginInvoke(form.Close);
            windowThread.Join(TimeSpan.FromSeconds(5));
            if (File.Exists(destination)) File.Delete(destination);
        }
    }
}
