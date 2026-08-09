using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class OfflineServerControllerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StartsAndParsesOnlyArmedOfflineState()
    {
        var paths = Paths();
        var runner = new FakeRunner(StateJson("d5d4f6c6-e4af-4ba8-bd92-c02c70b1c331"));
        var controller = new OfflineServerController(paths, runner);

        var state = await controller.StartAsync("d5d4f6c6-e4af-4ba8-bd92-c02c70b1c331", CancellationToken.None);

        Assert.True(state.Armed);
        Assert.True(state.OfflineProven);
        Assert.Equal("127.0.0.1:5542", state.Endpoint);
        Assert.DoesNotContain("SessionNonce", runner.Arguments, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionBecomesBlockedWithoutArmingInputWhenRetailClientIsIncompatible()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.ControlDirectory);
        await File.WriteAllTextAsync(paths.ClientCapabilityPath,
            """{"dedicatedOfflineCompatible":false,"blockerCode":"retail-client-blocked"}""");
        var controller = new OfflineServerController(paths, new LifecycleRunner(paths));
        var orchestrator = new QaSessionOrchestrator(paths, controller, new ReadyObserverFactory());

        var state = await orchestrator.StartAsync(CancellationToken.None);

        Assert.Equal(QaSessionPhase.BlockedPrerequisite, state.Phase);
        Assert.True(state.OfflineServerArmed);
        Assert.False(state.ClientInputArmed);
        Assert.Equal("retail-client-blocked", state.BlockerCode);
        Assert.NotNull(state.SessionNonceSha256);
        Assert.DoesNotContain(new string('A', 64), JsonSerializer.Serialize(state), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionFaultsBeforeArmingWhenObserverHealthFails()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.ControlDirectory);
        await File.WriteAllTextAsync(paths.ClientCapabilityPath,
            """{"dedicatedOfflineCompatible":true,"blockerCode":""}""");
        var controller = new OfflineServerController(paths, new LifecycleRunner(paths));
        var orchestrator = new QaSessionOrchestrator(paths, controller, new FailingObserverFactory());

        await Assert.ThrowsAsync<ObserverSpoolException>(() => orchestrator.StartAsync(CancellationToken.None));

        Assert.Equal(QaSessionPhase.Faulted, orchestrator.Status.Phase);
        Assert.False(orchestrator.Status.OfflineServerArmed);
        Assert.False(orchestrator.Status.ClientInputArmed);
        Assert.Equal("offline-session-start-failed", orchestrator.Status.BlockerCode);
    }

    [Fact]
    public async Task RefreshDisarmsWhenServerInspectionFails()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.ControlDirectory);
        await File.WriteAllTextAsync(paths.ClientCapabilityPath,
            """{"dedicatedOfflineCompatible":true,"blockerCode":""}""");
        var controller = new OfflineServerController(paths, new LifecycleRunner(paths, failInspect: true));
        var orchestrator = new QaSessionOrchestrator(paths, controller, new ReadyObserverFactory());
        await orchestrator.StartAsync(CancellationToken.None);

        var state = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.Equal(QaSessionPhase.Faulted, state.Phase);
        Assert.False(state.OfflineServerArmed);
        Assert.False(state.ClientInputArmed);
        Assert.Equal("offline-proof-lost", state.BlockerCode);
    }

    private QaPaths Paths()
    {
        Directory.CreateDirectory(root);
        var scripts = Path.Combine(root, "scripts");
        var control = Path.Combine(root, "control");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(control);
        return new(root, scripts, Path.Combine(root, "scenarios"), control,
            Path.Combine(root, "capability.json"), Path.Combine(root, "server.jar"), Path.Combine(root, "client.exe"));
    }

    private static string StateJson(string sessionId) => "docker diagnostic noise\n" + JsonSerializer.Serialize(new
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
        proof = new { }
    });

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FakeRunner(string output) : IQaProcessRunner
    {
        public List<string> Arguments { get; } = [];

        public Task<QaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            Arguments.AddRange(arguments);
            return Task.FromResult(new QaProcessResult(0, output, ""));
        }
    }

    private sealed class LifecycleRunner(QaPaths paths, bool failInspect = false) : IQaProcessRunner
    {
        public Task<QaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken cancellationToken)
        {
            if (failInspect && arguments.Any(value => value.EndsWith("Get-OfflineQaServer.ps1", StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new QaProcessResult(3, "", "offline proof lost"));
            var sessionIndex = arguments.ToList().FindIndex(value => value == "-SessionId");
            var sessionId = arguments[sessionIndex + 1];
            Directory.CreateDirectory(paths.ControlDirectory);
            File.WriteAllText(Path.Combine(paths.ControlDirectory, "session.json"), JsonSerializer.Serialize(new
            {
                sessionId,
                sessionNonce = new string('A', 64),
                fixtureUuid = "8f413a24-3ac2-52e3-8a59-499425cc5527",
                fixtureName = "HYTALE_QA_BOT",
                endpoint = "127.0.0.1:5542",
                createdAtUtc = DateTimeOffset.UtcNow
            }));
            return Task.FromResult(new QaProcessResult(0, StateJson(sessionId), ""));
        }
    }

    private sealed class ReadyObserverFactory : IObserverSpoolClientFactory
    {
        public IObserverSpoolClient Create(string spoolRoot, string sessionNonce) => new ReadyObserver();
    }

    private sealed class ReadyObserver : IObserverSpoolClient
    {
        public Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ObserverHeartbeat(true, true, "OFFLINE", "hash", true, 0, DateTimeOffset.UtcNow));

        public Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken) => Task.FromResult(
            new ObserverHealth(true, true, "OFFLINE", "DOCKER_SHARED_VOLUME_SPOOL", "hash", true));

        public Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FailingObserverFactory : IObserverSpoolClientFactory
    {
        public IObserverSpoolClient Create(string spoolRoot, string sessionNonce) => new FailingObserver();
    }

    private sealed class FailingObserver : IObserverSpoolClient
    {
        public Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken) =>
            throw new ObserverSpoolException("observer-heartbeat-stale", "stale");

        public Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
