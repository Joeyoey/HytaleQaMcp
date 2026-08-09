using System.Security.Cryptography;
using Hytale.Qa.Contracts;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class RollingVideoCaptureTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "hytale-qa-rolling-video-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RingIsBoundedAndProducesCanonicalAtomicHashChain()
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: 7);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), clock);
        var bundle = Path.Combine(root, "bounded-bundle");

        var result = await service.CaptureAsync(Identity(),
            new(bundle, FramesPerSecond: 2, RingSeconds: 2), stop.Token);

        Assert.Equal(RollingVideoCaptureStatus.Completed, result.Status);
        Assert.Equal(4, result.Manifest.MaximumFrames);
        Assert.Equal([4L, 5L, 6L, 7L], result.Manifest.Frames.Select(frame => frame.Sequence));
        Assert.Equal(4, Directory.GetFiles(bundle, "*.bmp").Length);
        Assert.True(RollingVideoEvidence.VerifyFrameChain(result.Manifest, out var rootHash));
        Assert.Equal(result.Manifest.ChainRootSha256, rootHash);
        Assert.Equal(result.Manifest.Frames[0].PreviousSha256, result.Manifest.ChainAnchorSha256);

        var manifestBytes = await File.ReadAllBytesAsync(result.ManifestPath);
        Assert.Equal(RollingVideoEvidence.WriteCanonicalManifest(result.Manifest), manifestBytes);
        Assert.Equal(Sha256(manifestBytes), result.ManifestSha256);
        Assert.Equal($"{result.ManifestSha256}\n",
            await File.ReadAllTextAsync(Path.Combine(bundle, "manifest.sha256")));
        Assert.True(RollingVideoEvidence.VerifyBundle(bundle, out var verificationCode));
        Assert.Equal("verified", verificationCode);
        Assert.Empty(TemporaryEntries(root));
    }

    [Theory]
    [InlineData("capture.window_minimized")]
    [InlineData("lease.invalid")]
    [InlineData("proof.stale")]
    public async Task SafetyLossStopsBeforeAnotherFrameAndSkipsEncoding(string failureCode)
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: 20);
        var guard = new SequenceGuard(failOnCall: 3, failureCode);
        var encoder = new FakeEncoder();
        var bundle = Path.Combine(root, $"safety-{failureCode.Replace('.', '-')}");
        var mp4 = Path.Combine(root, $"safety-{failureCode.Replace('.', '-')}.mp4");
        var service = new RollingWindowCaptureService(source, guard, new FakeClock(), encoder);

        var result = await service.CaptureAsync(Identity(), new(bundle, 10, 1, mp4), stop.Token);

        Assert.Equal(RollingVideoCaptureStatus.AbortedSafety, result.Status);
        Assert.Equal(failureCode, result.Code);
        Assert.Single(result.Manifest.Frames);
        Assert.Equal(1, source.Captures);
        Assert.Equal("capture-not-complete", result.Encoding.Code);
        Assert.Equal(0, encoder.Calls);
        Assert.False(File.Exists(mp4));
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task SafetyLossAfterCaptureDeletesUncommittedFrame()
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: 20);
        var bundle = Path.Combine(root, "post-capture-safety-loss");
        var service = new RollingWindowCaptureService(
            source,
            new SequenceGuard(failOnCall: 2, "capture.window_minimized"),
            new FakeClock());

        var result = await service.CaptureAsync(Identity(), new(bundle, 10, 1), stop.Token);

        Assert.Equal(RollingVideoCaptureStatus.AbortedSafety, result.Status);
        Assert.Empty(result.Manifest.Frames);
        Assert.Empty(Directory.GetFiles(bundle, "*.bmp"));
        Assert.True(RollingVideoEvidence.VerifyBundle(bundle, out _));
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task ExplicitAbortPublishesOnlyCommittedRawEvidenceAndSkipsMp4()
    {
        using var abort = new CancellationTokenSource();
        var source = new FakeFrameSource(abort, stopAfter: 1);
        var bundle = Path.Combine(root, "explicit-abort");
        var mp4 = Path.Combine(root, "explicit-abort.mp4");
        var encoder = new FakeEncoder();
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), new FakeClock(), encoder);

        var result = await service.CaptureAsync(
            Identity(), new(bundle, 1, 1, mp4), CancellationToken.None, abort.Token);

        Assert.Equal(RollingVideoCaptureStatus.Aborted, result.Status);
        Assert.Equal("capture-aborted", result.Code);
        Assert.Single(result.Manifest.Frames);
        Assert.Equal("capture-not-complete", result.Encoding.Code);
        Assert.Equal(0, encoder.Calls);
        Assert.True(RollingVideoEvidence.VerifyBundle(bundle, out _));
        Assert.False(File.Exists(mp4));
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task PartialFrameIsDeletedAndPriorRawEvidenceSurvivesCaptureFailure()
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: int.MaxValue, failOnCapture: 2);
        var bundle = Path.Combine(root, "source-failure");
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), new FakeClock());

        var result = await service.CaptureAsync(Identity(), new(bundle, 1, 10), stop.Token);

        Assert.Equal(RollingVideoCaptureStatus.Failed, result.Status);
        Assert.StartsWith("capture-failed:", result.Code, StringComparison.Ordinal);
        Assert.Single(result.Manifest.Frames);
        Assert.Single(Directory.GetFiles(bundle, "*.bmp"));
        Assert.True(File.Exists(result.ManifestPath));
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task EncodingFailurePreservesRawBundleAndDeletesIncompleteMp4()
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: 2);
        var encoder = new FakeEncoder(failAfterWriting: true);
        var bundle = Path.Combine(root, "encoding-failure");
        var mp4 = Path.Combine(root, "final.mp4");
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), new FakeClock(), encoder);

        var result = await service.CaptureAsync(Identity(), new(bundle, 2, 3, mp4), stop.Token);

        Assert.Equal(RollingVideoCaptureStatus.Completed, result.Status);
        Assert.False(result.Encoding.Succeeded);
        Assert.Equal("encoding-failed", result.Encoding.Code);
        Assert.Equal(2, Directory.GetFiles(bundle, "*.bmp").Length);
        Assert.True(File.Exists(result.ManifestPath));
        Assert.False(File.Exists(mp4));
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task EncoderReceivesOnlyBoundedSequenceAndPublishesMp4Atomically()
    {
        using var stop = new CancellationTokenSource();
        var source = new FakeFrameSource(stop, stopAfter: 5);
        var encoder = new FakeEncoder();
        var bundle = Path.Combine(root, "encoding-success");
        var mp4 = Path.Combine(root, "final.mp4");
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), new FakeClock(), encoder);

        var result = await service.CaptureAsync(Identity(), new(bundle, 2, 2, mp4), stop.Token);

        Assert.True(result.Encoding.Succeeded);
        Assert.Equal(1, encoder.Calls);
        Assert.NotNull(encoder.Request);
        Assert.Equal(2, encoder.Request!.FirstSequence);
        Assert.Equal(4, encoder.Request.FrameCount);
        Assert.Equal(2, encoder.Request.FramesPerSecond);
        Assert.EndsWith("frame-%010d.bmp", encoder.Request.FramePattern, StringComparison.Ordinal);
        Assert.True(File.Exists(mp4));
        Assert.Equal(Sha256(await File.ReadAllBytesAsync(mp4)), result.Encoding.Sha256);
        Assert.Empty(TemporaryEntries(root));
    }

    [Fact]
    public async Task AllowlistedEncoderRejectsHashMismatchBeforeStartingProcess()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test host path is unavailable.");
        var encoder = new AllowlistedFfmpegEncoder(new(executable, new string('0', 64)));
        var request = new RollingVideoEncodingRequest(
            Path.Combine(root, "frame-%010d.bmp"), 1, 1, 1, Path.Combine(root, "bad.mp4"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => encoder.EncodeAsync(request, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(request.DestinationPath));
    }

    [Fact]
    public async Task TamperedFrameEvidenceInvalidatesChain()
    {
        using var stop = new CancellationTokenSource();
        var service = new RollingWindowCaptureService(
            new FakeFrameSource(stop, stopAfter: 1),
            new SequenceGuard(),
            new FakeClock());
        var result = await service.CaptureAsync(Identity(),
            new(Path.Combine(root, "tamper"), 1, 1), stop.Token);
        var frames = result.Manifest.Frames.ToArray();
        frames[0] = frames[0] with { Sha256 = new string('f', 64) };

        Assert.False(RollingVideoEvidence.VerifyFrameChain(
            result.Manifest with { Frames = frames }, out _));
    }

    [Fact]
    public async Task TamperedBmpInvalidatesPublishedBundle()
    {
        using var stop = new CancellationTokenSource();
        var service = new RollingWindowCaptureService(
            new FakeFrameSource(stop, stopAfter: 1),
            new SequenceGuard(),
            new FakeClock());
        var bundle = Path.Combine(root, "file-tamper");
        var result = await service.CaptureAsync(Identity(), new(bundle, 1, 1), stop.Token);
        await File.AppendAllTextAsync(Path.Combine(bundle, result.Manifest.Frames[0].FileName), "tampered");

        Assert.False(RollingVideoEvidence.VerifyBundle(bundle, out var code));
        Assert.Equal("frame.file", code);
    }

    [Fact]
    public async Task ServiceRejectsASecondConcurrentWindowCapture()
    {
        using var stop = new CancellationTokenSource();
        var source = new BlockingFrameSource(stop);
        var service = new RollingWindowCaptureService(source, new SequenceGuard(), new FakeClock());
        var first = service.CaptureAsync(Identity(),
            new(Path.Combine(root, "first"), 1, 1), stop.Token);
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync(
            Identity(), new(Path.Combine(root, "second"), 1, 1), stop.Token));

        source.Release.TrySetResult();
        var result = await first;
        Assert.Equal(RollingVideoCaptureStatus.Completed, result.Status);
        Assert.False(Directory.Exists(Path.Combine(root, "second")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(11, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 301)]
    public async Task ConfigurationOutsideHardBoundsIsRejected(int fps, int seconds)
    {
        using var stop = new CancellationTokenSource();
        var service = new RollingWindowCaptureService(
            new FakeFrameSource(stop, 1), new SequenceGuard(), new FakeClock());

        await Assert.ThrowsAnyAsync<ArgumentOutOfRangeException>(() => service.CaptureAsync(
            Identity(), new(Path.Combine(root, $"invalid-{fps}-{seconds}"), fps, seconds), stop.Token));
        Assert.False(Directory.Exists(root));
    }

    private static ProcessIdentity Identity() => new(
        777,
        new DateTimeOffset(2026, 8, 8, 11, 0, 0, TimeSpan.Zero),
        @"C:\Games\Hytale\HytaleClient.exe",
        new string('a', 64),
        123456);

    private static IEnumerable<string> TemporaryEntries(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, "*.tmp", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFileSystemEntries(directory, "*.tmp.mp4", SearchOption.AllDirectories))
            : [];

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeFrameSource(
        CancellationTokenSource stop,
        int stopAfter,
        int? failOnCapture = null) : IRollingWindowFrameSource
    {
        public int Captures { get; private set; }

        public ValueTask<CaptureArtifact> CaptureAsync(
            ProcessIdentity identity,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Captures++;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var bytes = new byte[] { (byte)'B', (byte)'M', (byte)Captures, 0, 1, 2, 3, 4 };
            File.WriteAllBytes(destinationPath, bytes);
            if (Captures == failOnCapture) throw new IOException("deterministic-frame-failure");
            if (Captures == stopAfter) stop.Cancel();
            return ValueTask.FromResult(new CaptureArtifact(
                destinationPath,
                "image/bmp",
                320,
                180,
                DateTimeOffset.UnixEpoch,
                Sha256(bytes),
                new("windows-graphics-capture-window", CaptureAvailability.Available,
                    true, false, ["image/bmp"], "test")));
        }
    }

    private sealed class BlockingFrameSource(CancellationTokenSource stop) : IRollingWindowFrameSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CaptureArtifact> CaptureAsync(
            ProcessIdentity identity,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            var bytes = new byte[] { (byte)'B', (byte)'M', 0, 1 };
            File.WriteAllBytes(destinationPath, bytes);
            stop.Cancel();
            return new(
                destinationPath,
                "image/bmp",
                2,
                2,
                DateTimeOffset.UnixEpoch,
                Sha256(bytes),
                new("windows-graphics-capture-window", CaptureAvailability.Available,
                    true, false, ["image/bmp"], "test"));
        }
    }

    private sealed class SequenceGuard(int? failOnCall = null, string failureCode = "unsafe") : IRollingCaptureGuard
    {
        private int calls;

        public SafetyCheck Validate(ProcessIdentity identity)
        {
            calls++;
            return calls == failOnCall
                ? SafetyCheck.Fail(failureCode, "deterministic safety loss")
                : SafetyCheck.Pass();
        }
    }

    private sealed class FakeClock : IRollingCaptureClock
    {
        private DateTimeOffset now;

        public FakeClock(DateTimeOffset? initial = null) =>
            now = initial ?? new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset GetUtcNow() => now;

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            now += delay;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeEncoder(bool failAfterWriting = false) : IRollingVideoEncoder
    {
        public int Calls { get; private set; }
        public RollingVideoEncodingRequest? Request { get; private set; }

        public Task EncodeAsync(RollingVideoEncodingRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            File.WriteAllBytes(request.DestinationPath, [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
            if (failAfterWriting) throw new InvalidOperationException("deterministic-encoder-failure");
            return Task.CompletedTask;
        }
    }
}
