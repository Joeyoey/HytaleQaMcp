using System.Security.Cryptography;
using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Tests;

public sealed class WorkerControlServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-worker-control-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DockerProofCanNeverArmPhysicalInput()
    {
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(Paths(), new FakeWorkerClientFactory(rpc, []),
            new FakeProofFactory(new FakeProofProvider([])));

        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => control.AttachAsync(4242,
            Guid.NewGuid().ToString("D"), AssistanceMode.GuidedPhysical, Capabilities(), CancellationToken.None));

        Assert.Contains("docker-physical-attach-disabled", failure.Message, StringComparison.Ordinal);
        Assert.Empty(rpc.Methods);
    }

    [Fact]
    public async Task VerifiesProofBeforeWorkerStartAndArmsSingleProcessPolicy()
    {
        var paths = Paths();
        var order = new List<string>();
        var proof = new FakeProofProvider(order);
        var rpc = new FakeWorkerClient();
        var factory = new FakeWorkerClientFactory(rpc, order);
        await using var control = new WorkerControlService(paths, factory, new FakeProofFactory(proof));

        var state = await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);

        Assert.Equal(WorkerControlPhase.Armed, state.Phase);
        Assert.Equal(["proof", "worker-start"], order.Take(2));
        Assert.NotNull(rpc.AcquiredPolicy);
        Assert.True(rpc.AcquiredPolicy!.RequireSingleMatchingProcess);
        Assert.Equal("HytaleClient.exe", rpc.AcquiredPolicy.ExpectedExecutableName);
        Assert.False(rpc.CapabilitiesRequestedAfterAcquire);

        await control.DetachAsync(CancellationToken.None);
        Assert.True(rpc.Terminated);
        Assert.Contains("input.releaseAll", rpc.Methods);
        Assert.Contains("lease.release", rpc.Methods);
    }

    [Fact]
    public async Task ProofLossReleasesAndTerminatesWorker()
    {
        var paths = Paths();
        var proof = new FakeProofProvider([]) { FailAfterCalls = 1 };
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(paths,
            new FakeWorkerClientFactory(rpc, []), new FakeProofFactory(proof));
        await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!rpc.Terminated)
            await Task.Delay(25, timeout.Token);

        Assert.Equal(WorkerControlPhase.Faulted, control.State.Phase);
        Assert.Equal("offline-proof-lost", control.State.FaultCode);
        Assert.Contains("input.releaseAll", rpc.Methods);
        Assert.Contains("audio.abort", rpc.Methods);
    }

    [Fact]
    public async Task RollingCaptureIsAbortedWhenOfflineProofIsLost()
    {
        var proof = new FakeProofProvider([]) { FailAfterCalls = 1 };
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(Paths(), new FakeWorkerClientFactory(rpc, []),
            new FakeProofFactory(proof));
        await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);
        var started = await control.RollingStartAsync("proof_loss", 2, 10, false, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!rpc.Terminated) await Task.Delay(25, timeout.Token);

        Assert.Equal("rolling-test", started.GetProperty("captureId").GetString());
        Assert.Contains("rolling.abort", rpc.Methods);
        Assert.Contains("input.releaseAll", rpc.Methods);
    }

    [Fact]
    public async Task RollingOptionsAreRejectedBeforeWorkerRpcAndFaultedStopClearsControlState()
    {
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(Paths(), new FakeWorkerClientFactory(rpc, []),
            new FakeProofFactory(new FakeProofProvider([])));
        await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            control.RollingStartAsync("invalid", 0, 10, false, CancellationToken.None));
        Assert.DoesNotContain("rolling.start", rpc.Methods);

        _ = await control.RollingStartAsync("first", 2, 10, false, CancellationToken.None);
        rpc.FailRollingStop = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.RollingStopAsync("rolling-test", CancellationToken.None));
        rpc.FailRollingStop = false;
        _ = await control.RollingStartAsync("second", 2, 10, false, CancellationToken.None);
        Assert.Equal(2, rpc.Methods.Count(method => method == "rolling.start"));
    }

    [Fact]
    public async Task SemanticNavigationEmitsOnlyBoundedPhysicalInput()
    {
        var paths = Paths();
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(paths,
            new FakeWorkerClientFactory(rpc, []), new FakeProofFactory(new FakeProofProvider([])));
        await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);

        var result = await control.NavigateStepAsync(new(
            System.Numerics.Vector3.Zero, 0, new(0, 0, -10), true, false, TimeSpan.Zero),
            CancellationToken.None);

        Assert.Equal("navigate", result.Kind);
        Assert.Contains("input.key", rpc.Methods);
        Assert.DoesNotContain(rpc.Methods, method => method.Contains("damage", StringComparison.OrdinalIgnoreCase) ||
            method.Contains("teleport", StringComparison.OrdinalIgnoreCase) || method.Contains("packet", StringComparison.OrdinalIgnoreCase));
        var keyCalls = rpc.Parameters.Where(call => call.Method == "input.key").Select(call => call.Value).ToArray();
        Assert.Equal(2, keyCalls.Length);
        Assert.Equal(0x57, keyCalls[0].GetProperty("virtualKey").GetInt32());
    }

    [Fact]
    public async Task ArtifactNamesCannotEscapeIsolatedDirectory()
    {
        var paths = Paths();
        var rpc = new FakeWorkerClient();
        await using var control = new WorkerControlService(paths,
            new FakeWorkerClientFactory(rpc, []), new FakeProofFactory(new FakeProofProvider([])));
        await control.AttachLauncherAsync(4242, "proof.json", AssistanceMode.GuidedPhysical,
            Capabilities(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            control.ScreenshotAsync("..\\escaped.bmp", CancellationToken.None));
        Assert.DoesNotContain("capture.screenshot", rpc.Methods);
    }

    [Fact]
    public async Task DockerInspectIsOneTimeGateAndDoesNotEnterHeartbeatCadence()
    {
        var paths = Paths();
        File.WriteAllBytes(paths.ServerJarPath, [9, 8, 7, 6]);
        var sessionId = Guid.NewGuid().ToString("D");
        var runner = new SlowInspectRunner(sessionId, TimeSpan.FromMilliseconds(300));
        var servers = new OfflineServerController(paths, runner);
        var secret = new OfflineSessionSecret(sessionId, new string('A', 64), Guid.NewGuid().ToString("D"),
            "HYTALE_QA_BOT", "127.0.0.1:5542", DateTimeOffset.UtcNow);
        var observer = new ReadyObserver();
        var provider = new DockerWorkerOfflineProofProvider(paths, servers, observer, secret);

        _ = await provider.GetFreshProofAsync(CancellationToken.None);
        var began = System.Diagnostics.Stopwatch.StartNew();
        _ = await provider.GetFreshProofAsync(CancellationToken.None);
        began.Stop();

        Assert.Equal(1, runner.InspectCalls);
        Assert.Equal(1, observer.HealthCalls);
        Assert.Equal(2, observer.HeartbeatCalls);
        Assert.True(began.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Steady-state proof took {began.Elapsed}; Docker inspect leaked into heartbeat cadence.");
    }

    private QaPaths Paths()
    {
        Directory.CreateDirectory(root);
        var client = Path.Combine(root, "HytaleClient.exe");
        File.WriteAllBytes(client, [1, 2, 3, 4]);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(client)));
        var capability = Path.Combine(root, "capability.json");
        File.WriteAllText(capability, JsonSerializer.Serialize(new { clientSha256 = hash }));
        return new(root, Path.Combine(root, "scripts"), Path.Combine(root, "scenarios"),
            Path.Combine(root, "control"), capability, Path.Combine(root, "server.jar"), client)
        {
            DockerProject = "sample-hytale-qa-offline",
            DockerContainer = "sample-hytale-qa-offline",
            DockerNetwork = "sample-hytale-qa-offline-net"
        };
    }

    private static EvidenceCapabilities Capabilities() => new(true, true, true, false, false, false);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeProofFactory(IWorkerOfflineProofProvider provider) : IWorkerOfflineProofProviderFactory
    {
        public IWorkerOfflineProofProvider Create(string sessionId) => provider;
        public IWorkerOfflineProofProvider CreateLauncher(string evidenceFileName) => provider;
    }

    private sealed class FakeProofProvider(List<string> order) : IWorkerOfflineProofProvider
    {
        private int calls;
        public int FailAfterCalls { get; init; } = int.MaxValue;
        public OfflineProofBoundaryKind Boundary => OfflineProofBoundaryKind.LauncherOwnedSingleplayer;
        public string SessionId { get; } = Guid.NewGuid().ToString("D");

        public Task<OfflineServerProof> GetFreshProofAsync(CancellationToken cancellationToken)
        {
            order.Add("proof");
            if (++calls > FailAfterCalls) throw new InvalidOperationException("proof lost for test");
            var started = DateTimeOffset.UtcNow.AddSeconds(-3);
            var evidence = new LauncherSingleplayerEvidence(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                new string('1', 64), new string('2', 64),
                new(100, 1, started.AddSeconds(-2), "launcher.exe", new string('3', 64)),
                new(4242, 100, started.AddSeconds(-1), "HytaleClient.exe", new string('4', 64)),
                new(500, 4242, started, "java.exe", new string('5', 64)),
                new string('B', 64), new string('6', 64), new string('7', 64), true, true, true);
            return Task.FromResult(new OfflineServerProof("offline", true, "127.0.0.1:7788",
                "", "", "", new string('B', 64), new string('A', 64), DateTimeOffset.UtcNow,
                OfflineProofKind.LauncherOwnedSingleplayer, evidence));
        }
    }

    private sealed class FakeWorkerClientFactory(FakeWorkerClient client, List<string> order) : IWorkerRpcClientFactory
    {
        public Task<IWorkerRpcClient> StartAsync(string tracePath, string artifactRoot, CancellationToken cancellationToken)
        {
            Assert.Equal(Path.GetDirectoryName(tracePath), artifactRoot);
            order.Add("worker-start");
            return Task.FromResult<IWorkerRpcClient>(client);
        }
    }

    private sealed class FakeWorkerClient : IWorkerRpcClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        public bool IsAlive => !Terminated;
        public bool Terminated { get; private set; }
        public List<string> Methods { get; } = [];
        public List<(string Method, JsonElement Value)> Parameters { get; } = [];
        public SessionSafetyPolicy? AcquiredPolicy { get; private set; }
        public bool CapabilitiesRequestedAfterAcquire { get; private set; }
        public bool FailRollingStop { get; set; }

        public Task<T> CallAsync<T>(string method, object? parameters, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (method == "rolling.stop" && FailRollingStop)
                throw new InvalidOperationException("simulated rolling stop fault");
            Methods.Add(method);
            var serialized = JsonSerializer.SerializeToElement(parameters, JsonOptions);
            Parameters.Add((method, serialized));
            object value;
            switch (method)
            {
                case "worker.capabilities":
                    CapabilitiesRequestedAfterAcquire = AcquiredPolicy is not null;
                    value = new WorkerCapabilities("test", "windows", false, true, true, true, true,
                        new("wgc", CaptureAvailability.Available, true, false, ["image/bmp"], ""),
                        new("process", CaptureAvailability.Available, true, true, ["audio/wav"], 20348, ""));
                    break;
                case "lease.acquire":
                    AcquiredPolicy = serialized.GetProperty("policy").Deserialize<SessionSafetyPolicy>(JsonOptions)!;
                    value = new ClientLease("lease-test", DateTimeOffset.UtcNow,
                        new(4242, DateTimeOffset.UtcNow, "HytaleClient.exe", new string('C', 64), 100), AcquiredPolicy);
                    break;
                case "session.arm":
                case "session.heartbeat":
                    value = new SessionState(AcquiredPolicy!.SessionId, "lease-test", true,
                        AcquiredPolicy.AssistanceMode, Capabilities(),
                        serialized.GetProperty("offlineProof").Deserialize<OfflineServerProof>(JsonOptions)!, DateTimeOffset.UtcNow);
                    break;
                case "safety.check":
                    value = SafetyCheck.Pass();
                    break;
                case "audio.state":
                case "audio.abort":
                    value = new AudioCaptureState(null, AudioCaptureStatus.Idle, null, null, null, null,
                        new("process", CaptureAvailability.Available, true, true, ["audio/wav"], 20348, ""));
                    break;
                case "rolling.start":
                    value = JsonSerializer.SerializeToElement(new { captureId = "rolling-test", status = "capturing" });
                    break;
                case "rolling.state":
                    value = JsonSerializer.SerializeToElement(new { captureId = "rolling-test", status = "capturing" });
                    break;
                case "rolling.stop":
                case "rolling.abort":
                    value = JsonSerializer.SerializeToElement(new { status = method == "rolling.stop" ? "completed" : "aborted" });
                    break;
                default:
                    value = JsonSerializer.SerializeToElement(new { sent = true, released = true, focused = true });
                    break;
            }
            return Task.FromResult((T)value);
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Terminated = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Terminated = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SlowInspectRunner(string sessionId, TimeSpan delay) : IQaProcessRunner
    {
        public int InspectCalls { get; private set; }

        public async Task<QaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
        {
            InspectCalls++;
            await Task.Delay(delay, cancellationToken);
            return new(0, JsonSerializer.Serialize(new
            {
                schema = "hytale-qa-offline-qa-server-state-v1",
                sessionId,
                endpoint = "127.0.0.1:5542",
                projectName = "sample-hytale-qa-offline",
                serviceName = "hytale-qa-offline",
                containerName = "sample-hytale-qa-offline",
                offlineProven = true,
                observerReady = true,
                armed = true,
                proof = new { loopbackOnly = true, offlineProven = true, armed = true }
            }), "");
        }
    }

    private sealed class ReadyObserver : IObserverSpoolClient
    {
        public int HeartbeatCalls { get; private set; }
        public int HealthCalls { get; private set; }
        public Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken)
        {
            HeartbeatCalls++;
            return Task.FromResult(new ObserverHeartbeat(true, true, "OFFLINE", "hash", true, 1, DateTimeOffset.UtcNow));
        }
        public Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken)
        {
            HealthCalls++;
            return Task.FromResult(new ObserverHealth(true, true, "OFFLINE", "DOCKER_SHARED_VOLUME_SPOOL", "hash", true));
        }
        public Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
