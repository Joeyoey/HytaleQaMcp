using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Tests;

public sealed class QaRunCoordinatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-run-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ScenarioPersistsCursorPinsReportAndResult()
    {
        WriteScenario("scenario.one");
        var paths = Paths();
        var factory = new FakeRuntimeFactory();
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            factory, new ReadyProofValidator(777), new DetachedWorker());

        var started = await coordinator.StartScenarioAsync("scenario.one", 777, "launch.json", CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.Completed, result.Phase);
        Assert.Equal(["scenario.one"], factory.ExecutionOrder);
        Assert.Single(result.Scenarios);
        Assert.All(result.Scenarios, report => Assert.Equal(QaRunOutcome.Passed, report.Outcome));
        Assert.Single(result.ScenarioCanonicalSha256);
        Assert.Equal(777, result.RuntimePins!.ClientProcessId);
        Assert.True(File.Exists(Path.Combine(result.EvidenceDirectory, "state.json")));
        Assert.True(File.Exists(Path.Combine(result.EvidenceDirectory, "runtime-pins.json")));
        Assert.Single(Directory.EnumerateFiles(result.EvidenceDirectory, "scenario-*.json"));
        Assert.NotNull(result.ArtifactManifestPath);
        Assert.True(File.Exists(result.ArtifactManifestPath));
        Assert.Matches("^[0-9A-F]{64}$", result.ArtifactManifestSha256);
        Assert.Empty(result.ArtifactGaps);
        File.AppendAllText(result.ArtifactManifestPath!, " ");
        Assert.Throws<InvalidDataException>(() => coordinator.Result(completed.RunId!));
    }

    [Fact]
    public async Task FreshSessionSuitePausesForNewProofAndRetainsAuthenticatedPins()
    {
        WriteScenario("scenario.one");
        WriteScenario("scenario.two");
        WriteSuite("suite.serial", ["scenario.one", "scenario.two"]);
        var paths = Paths();
        var factory = new FakeRuntimeFactory();
        var proofs = new ReadyProofValidator(("first.json", 777), ("second.json", 778));
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            factory, proofs, new DetachedWorker());

        var started = await coordinator.StartSuiteAsync(
            "suite.serial", 777, "first.json", CancellationToken.None);
        await WaitForPhase(coordinator, QaCoordinatedRunPhase.AwaitingHandoff);

        Assert.Equal(["scenario.one"], factory.ExecutionOrder);
        Assert.Equal(1, coordinator.State.CompletedScenarios);
        Assert.Equal("scenario.two", coordinator.State.CurrentScenarioId);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.HandoffSuiteAsync(
            started.RunId!, 777, "first.json", CancellationToken.None));
        Assert.Equal(QaCoordinatedRunPhase.AwaitingHandoff, coordinator.State.Phase);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.HandoffSuiteAsync(
            started.RunId!, 999, "second.json", CancellationToken.None));
        Assert.Equal(QaCoordinatedRunPhase.AwaitingHandoff, coordinator.State.Phase);
        proofs.Override("ambiguous.json", ReadyProofValidator.Summary("ambiguous.json", 779) with
            { ClientProcessCount = 2 });
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.HandoffSuiteAsync(
            started.RunId!, 779, "ambiguous.json", CancellationToken.None));
        Assert.Equal(QaCoordinatedRunPhase.AwaitingHandoff, coordinator.State.Phase);

        _ = await coordinator.HandoffSuiteAsync(started.RunId!, 778, "second.json", CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.Completed, result.Phase);
        Assert.Equal(["scenario.one", "scenario.two"], factory.ExecutionOrder);
        Assert.Equal(2, result.ScenarioRuntimePins.Count);
        Assert.Equal([777, 778], result.ScenarioRuntimePins.Select(item => item.Pins.ClientProcessId));
        Assert.Equal(2, result.ScenarioRuntimePins.Select(item => item.Pins.SessionId).Distinct().Count());
        Assert.All(result.ScenarioRuntimePins, item =>
        {
            Assert.True(File.Exists(item.PinsPath));
            Assert.True(File.Exists(item.AuthenticationPath));
        });
        var secondSeal = File.ReadAllBytes(result.ScenarioRuntimePins[1].AuthenticationPath);
        File.Copy(result.ScenarioRuntimePins[0].AuthenticationPath,
            result.ScenarioRuntimePins[1].AuthenticationPath, overwrite: true);
        Assert.Throws<InvalidDataException>(() => coordinator.Result(completed.RunId!));
        File.WriteAllBytes(result.ScenarioRuntimePins[1].AuthenticationPath, secondSeal);
        File.AppendAllText(result.ScenarioRuntimePins[0].PinsPath, " ");
        Assert.Throws<InvalidDataException>(() => coordinator.Result(completed.RunId!));
    }

    [Fact]
    public async Task LifecycleStepPausesAndAcceptsOnlyRenewedProofForSameClientAndWorld()
    {
        WriteLifecycleScenario("scenario.reconnect");
        var paths = Paths();
        var first = ReadyProofValidator.Summary("first.json", 777);
        var renewed = first with
        {
            EvidenceId = Guid.NewGuid().ToString("D"),
            SessionId = Guid.NewGuid().ToString("D"),
            LaunchEvidenceSha256 = new string('B', 64),
            ServerProcessId = first.ServerProcessId + 100,
            ObservedAtUtc = DateTimeOffset.UtcNow
        };
        var proofs = new ReadyProofValidator(("first.json", 777), ("renewed.json", 777));
        proofs.Override("first.json", first);
        proofs.Override("renewed.json", renewed);
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths),
            new SuiteCatalog(paths), new LifecycleRuntimeFactory(first), proofs, new DetachedWorker());

        var started = await coordinator.StartScenarioAsync(
            "scenario.reconnect", 777, "first.json", CancellationToken.None);
        await WaitForPhase(coordinator, QaCoordinatedRunPhase.AwaitingHandoff);

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.HandoffRunAsync(
            started.RunId!, 778, "renewed.json", CancellationToken.None));
        Assert.Equal(QaCoordinatedRunPhase.AwaitingHandoff, coordinator.State.Phase);
        _ = await coordinator.HandoffRunAsync(
            started.RunId!, 777, "renewed.json", CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.Completed, completed.Phase);
        Assert.Equal(QaRunOutcome.Passed, coordinator.Result(started.RunId!).Scenarios.Single().Outcome);
    }

    [Fact]
    public async Task HandoffProofChangingBetweenSubmissionAndExecutionFailsInvalidEvidence()
    {
        WriteScenario("scenario.one");
        WriteScenario("scenario.two");
        WriteSuite("suite.serial", ["scenario.one", "scenario.two"]);
        var paths = Paths();
        var stable = ReadyProofValidator.Summary("first.json", 777);
        var before = ReadyProofValidator.Summary("second.json", 778);
        var changed = before with { EvidenceId = Guid.NewGuid().ToString("D") };
        var proofs = new SequencedProofValidator(new Dictionary<string, LauncherProofSummary[]>
        {
            ["first.json"] = [stable],
            ["second.json"] = [before, changed]
        });
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            new FakeRuntimeFactory(), proofs, new DetachedWorker());

        var started = await coordinator.StartSuiteAsync(
            "suite.serial", 777, "first.json", CancellationToken.None);
        await WaitForPhase(coordinator, QaCoordinatedRunPhase.AwaitingHandoff);
        _ = await coordinator.HandoffSuiteAsync(started.RunId!, 778, "second.json", CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.InvalidEvidence, result.Phase);
        Assert.Equal("handoff-invalid-evidence", result.Code);
        Assert.Contains("handoff-proof-changed-during-acceptance", result.Message, StringComparison.Ordinal);
        Assert.Single(result.Scenarios);
        Assert.Single(result.ScenarioRuntimePins);
    }

    [Fact]
    public async Task CancellationWhileAwaitingHandoffPreservesCompletedScenarioEvidence()
    {
        WriteScenario("scenario.one");
        WriteScenario("scenario.two");
        WriteSuite("suite.serial", ["scenario.one", "scenario.two"]);
        var paths = Paths();
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            new FakeRuntimeFactory(), new ReadyProofValidator(("first.json", 777)), new DetachedWorker());

        var started = await coordinator.StartSuiteAsync(
            "suite.serial", 777, "first.json", CancellationToken.None);
        await WaitForPhase(coordinator, QaCoordinatedRunPhase.AwaitingHandoff);
        _ = await coordinator.CancelAsync(started.RunId!, CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.Blocked, result.Phase);
        Assert.Equal("run-cancelled", result.Code);
        Assert.Single(result.Scenarios);
        Assert.Single(result.ScenarioRuntimePins);
        Assert.True(File.Exists(result.ScenarioRuntimePins[0].PinsPath));
        Assert.False(File.Exists(Path.Combine(paths.RunDirectory, "active-run.lease.json")));
    }

    [Fact]
    public async Task ExclusiveLeaseRejectsParallelRunAndCancellationStillPersistsResult()
    {
        WriteScenario("scenario.blocking");
        var paths = Paths();
        var factory = new FakeRuntimeFactory(block: true);
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            factory, new ReadyProofValidator(888), new DetachedWorker());
        var started = await coordinator.StartScenarioAsync("scenario.blocking", 888, "launch.json", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartScenarioAsync(
            "scenario.blocking", 888, "launch.json", CancellationToken.None));
        _ = await coordinator.CancelAsync(started.RunId!, CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal("run-cancelled", result.Code);
        Assert.True(result.TeardownAttempted);
        Assert.True(result.TeardownSucceeded);
        Assert.False(File.Exists(Path.Combine(paths.RunDirectory, "active-run.lease.json")));
    }

    [Fact]
    public async Task NonPassingScenarioProducesNonCompletedTopLevelPhase()
    {
        WriteScenario("scenario.untested");
        var paths = Paths();
        await using var coordinator = new QaRunCoordinator(paths, new ScenarioCatalog(paths), new SuiteCatalog(paths),
            new FakeRuntimeFactory(outcome: QaRunOutcome.Untested), new ReadyProofValidator(999), new DetachedWorker());

        var started = await coordinator.StartScenarioAsync("scenario.untested", 999, "launch.json", CancellationToken.None);
        var completed = await WaitForCompletion(coordinator, started.RunId!);
        var result = coordinator.Result(completed.RunId!);

        Assert.Equal(QaCoordinatedRunPhase.Untested, result.Phase);
        Assert.Equal("scenario-untested", result.Code);
        Assert.False(result.Scenarios.Single().Passed);
        Assert.False(paths.RunDirectory.StartsWith(paths.LauncherControlDirectory, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<QaCoordinatedRunState> WaitForCompletion(QaRunCoordinator coordinator, string runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (coordinator.State.Phase is QaCoordinatedRunPhase.Running or
            QaCoordinatedRunPhase.AwaitingHandoff or QaCoordinatedRunPhase.Cancelling)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(runId, coordinator.State.RunId);
        return coordinator.State;
    }

    private static async Task WaitForPhase(QaRunCoordinator coordinator, QaCoordinatedRunPhase phase)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (coordinator.State.Phase != phase)
        {
            if (coordinator.State.Phase is QaCoordinatedRunPhase.Completed or QaCoordinatedRunPhase.Failed or
                QaCoordinatedRunPhase.Blocked or QaCoordinatedRunPhase.Untested or
                QaCoordinatedRunPhase.InvalidEvidence or QaCoordinatedRunPhase.AbortedSafety or
                QaCoordinatedRunPhase.Faulted)
                throw new Xunit.Sdk.XunitException($"Run ended as {coordinator.State.Phase} before {phase}.");
            await Task.Delay(10, timeout.Token);
        }
    }

    private QaPaths Paths() => new(root, Path.Combine(root, "scripts"), Path.Combine(root, "qa-automation", "scenarios"),
        Path.Combine(root, "docker-control"), Path.Combine(root, "capability.json"), Path.Combine(root, "server.jar"),
        Path.Combine(root, "HytaleClient.exe"));

    private void WriteScenario(string id)
    {
        var directory = Path.Combine(root, "qa-automation", "scenarios");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new
        {
            schema = "hytale-qa/v1", id, description = "test", tags = new[] { "test" }, mode = "guided_physical",
            capabilities = new[] { "physical_input" },
            fixture = new
            {
                serverProfile = "hytale-qa-offline", snapshot = (string?)null, worldSeed = 1, runSeed = 2,
                player = new { uuid = Guid.NewGuid(), name = "QA" },
                client = new { width = 800, height = 600, displayMode = "borderless", hudScale = 1, fov = 80,
                    keybindProfile = "test", graphicsProfile = (string?)null }, loadout = (string?)null
            },
            safety = new { offlineRequired = true, loopbackOnly = true, maximumClientProcesses = 1,
                abortOnFocusLoss = true, abortOnEndpointDrift = true, forbiddenCapabilities = Array.Empty<string>() },
            budgets = new { totalSeconds = 10, stepSeconds = 5, noProgressSeconds = 2, maximumDeaths = 0, maximumRecoveries = 0 },
            steps = new[] { new { id = "start", operation = "session.start" }, new { id = "stop", operation = "session.stop" } },
            artifacts = new { screenshots = "none" }
        }));
    }

    private void WriteLifecycleScenario(string id)
    {
        WriteScenario(id);
        var path = Path.Combine(root, "qa-automation", "scenarios", id + ".json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var rootNode = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
        rootNode["steps"] = new System.Text.Json.Nodes.JsonArray(
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"start\",\"operation\":\"session.start\"}"),
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"reconnect\",\"operation\":\"client.reconnect\",\"expect\":{\"sameClientPid\":true}}"),
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"stop\",\"operation\":\"session.stop\"}"));
        File.WriteAllText(path, rootNode.ToJsonString());
    }

    private void WriteSuite(string id, string[] scenarioIds)
    {
        var directory = Path.Combine(root, "qa-automation", "suites");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(new
        {
            schema = "hytale-qa-suite/v1", id, purpose = "smoke", coverage = new[] { "test" },
            execution = new { order = "listed", maximumParallelClients = 1, freshSessionPerScenario = true,
                requireOfflineProof = true, failFast = false }, scenarios = scenarioIds,
            releaseCriteria = new { requireAllPass = true, allowUntested = false, allowWhiteBoxForPlayerFacing = false,
                requiredEvidence = Array.Empty<string>() }
        }));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class ReadyProofValidator : ILauncherProofValidator
    {
        private const string Protected = "test-protected";
        private readonly Dictionary<string, LauncherProofSummary> summaries;

        public ReadyProofValidator(int pid) : this(("launch.json", pid)) { }
        public ReadyProofValidator(params (string FileName, int Pid)[] proofs) => summaries =
            proofs.ToDictionary(item => item.FileName, item => Summary(item.FileName, item.Pid),
                StringComparer.Ordinal);

        public void Override(string fileName, LauncherProofSummary summary) => summaries[fileName] = summary;

        public static LauncherProofSummary Summary(string fileName, int pid)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileName));
            var evidence = new Guid(bytes[..16]).ToString("D");
            bytes[0] ^= 0x5A;
            var session = new Guid(bytes[..16]).ToString("D");
            bytes[0] ^= 0xA5;
            var world = new Guid(bytes[..16]).ToString("D");
            return new(evidence, session, world, $"127.0.0.1:{7000 + pid % 1000}", pid - 1, pid,
                pid + 1, Convert.ToHexString(bytes), DateTimeOffset.UtcNow, 1, true);
        }

        public Task<LauncherProofSummary> ValidateAsync(string evidenceFileName, CancellationToken cancellationToken) =>
            Task.FromResult(summaries[evidenceFileName] with { ObservedAtUtc = DateTimeOffset.UtcNow });
        public LauncherEvidenceSeal Seal(string evidenceFileName, string purpose, string sha256) =>
            new("hytale-qa-evidence-seal-v1", purpose, summaries[evidenceFileName].SessionId,
                summaries[evidenceFileName].EvidenceId,
                sha256, Protected, "test-mac:" + purpose + ":" + sha256);
        public void VerifySeal(LauncherEvidenceSeal seal, string purpose, string sha256)
        {
            if (seal.Mac != "test-mac:" + purpose + ":" + sha256)
                throw new InvalidDataException("test seal mismatch");
        }
    }

    private sealed class SequencedProofValidator(
        IReadOnlyDictionary<string, LauncherProofSummary[]> sequences) : ILauncherProofValidator
    {
        private readonly Dictionary<string, int> cursors = [];
        public Task<LauncherProofSummary> ValidateAsync(string evidenceFileName,
            CancellationToken cancellationToken)
        {
            var values = sequences[evidenceFileName];
            cursors.TryGetValue(evidenceFileName, out var cursor);
            cursors[evidenceFileName] = cursor + 1;
            return Task.FromResult(values[Math.Min(cursor, values.Length - 1)] with
                { ObservedAtUtc = DateTimeOffset.UtcNow });
        }
        public LauncherEvidenceSeal Seal(string evidenceFileName, string purpose, string sha256)
        {
            var value = sequences[evidenceFileName][0];
            return new("hytale-qa-evidence-seal-v1", purpose, value.SessionId, value.EvidenceId,
                sha256, "test-protected", "test-mac:" + purpose + ":" + sha256);
        }
        public void VerifySeal(LauncherEvidenceSeal seal, string purpose, string sha256)
        {
            if (seal.Mac != "test-mac:" + purpose + ":" + sha256)
                throw new InvalidDataException("test seal mismatch");
        }
    }

    private sealed class FakeRuntimeFactory(bool block = false, QaRunOutcome? outcome = null) : IHytaleScenarioRuntimeFactory
    {
        public List<string> ExecutionOrder { get; } = [];
        public IQaScenarioRuntime Create(QaScenario scenario, int clientProcessId, string launcherEvidenceFileName,
            string artifactDirectory) => new FakeRuntime(scenario.Id, clientProcessId, ExecutionOrder, block, outcome);
    }

    private sealed class LifecycleRuntimeFactory(LauncherProofSummary first) : IHytaleScenarioRuntimeFactory
    {
        public IQaScenarioRuntime Create(QaScenario scenario, int clientProcessId,
            string launcherEvidenceFileName, string artifactDirectory) =>
            new LifecycleRuntime(clientProcessId, launcherEvidenceFileName, first);
    }

    private sealed class LifecycleRuntime(int pid, string evidenceFile, LauncherProofSummary first)
        : IQaScenarioRuntime, IQaLifecycleHandoffRuntime
    {
        private Func<QaLifecycleHandoffRequest, CancellationToken,
            Task<QaLifecycleHandoffSubmission>>? handoff;
        private bool stopped;
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        public void ConfigureLifecycleHandoff(Func<QaLifecycleHandoffRequest, CancellationToken,
            Task<QaLifecycleHandoffSubmission>> value) => handoff = value;
        public Task<QaSafetySnapshot> SafetyAsync(CancellationToken cancellationToken) => Task.FromResult(
            new QaSafetySnapshot(true, true, true, true, true, stopped ? 0 : 1,
                stopped ? null : pid, stopped ? null : started, true, true, false, false, 0));
        public async Task<QaStepResult> ExecuteAsync(QaScenarioStep step, CancellationToken cancellationToken)
        {
            if (step.Operation == "client.reconnect")
            {
                if (handoff is null) return new(false, "missing-handoff", "missing", false, QaRunOutcome.Blocked);
                var result = await handoff(new QaLifecycleHandoffRequest(
                    step.Operation, pid, evidenceFile, first.EvidenceId, first.SessionId, first.WorldId,
                    new string('A', 64), first.ServerProcessId, DateTimeOffset.UtcNow), cancellationToken);
                return result.ClientProcessId == pid
                    ? new(true, "handoff-ok", "ok")
                    : new(false, "pid-changed", "changed", false, QaRunOutcome.AbortedSafety);
            }
            if (step.Operation == "session.stop") stopped = true;
            return new(true, "ok", "ok");
        }
        public Task ReleaseAllInputsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRuntime(string scenarioId, int pid, List<string> order, bool block, QaRunOutcome? outcome) : IQaScenarioRuntime
    {
        private bool stopped;
        private readonly DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
        public Task<QaSafetySnapshot> SafetyAsync(CancellationToken cancellationToken) => Task.FromResult(new QaSafetySnapshot(
            true, true, true, true, true, stopped ? 0 : 1, stopped ? null : pid, stopped ? null : started,
            true, true, false, false, 0));
        public async Task<QaStepResult> ExecuteAsync(QaScenarioStep step, CancellationToken cancellationToken)
        {
            if (step.Operation == "session.start")
            {
                order.Add(scenarioId);
                if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                if (outcome is not null)
                    return new(false, "simulated-" + outcome.Value.ToString().ToLowerInvariant(),
                        "simulated non-passing result", false, outcome);
            }
            if (step.Operation == "session.stop") stopped = true;
            return new(true, "ok", "ok");
        }
        public Task ReleaseAllInputsAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class DetachedWorker : IWorkerControlService
    {
        public WorkerControlState State { get; } = new(WorkerControlPhase.Detached, null, null, null, null, null, "detached");
        public Task<WorkerControlState> DetachAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<WorkerControlState> AttachAsync(int processId, string sessionId, AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkerControlState> AttachLauncherAsync(int processId, string evidenceFileName, AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task StopDueToProofLossAsync(string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SafetyCheck> SafetyAsync(bool requireForeground, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> FocusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CaptureArtifact> ScreenshotAsync(string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioCaptureState> AudioStartAsync(string fileName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioCaptureState> AudioStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioCaptureArtifact> AudioStopAsync(string captureId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AudioCaptureState> AudioAbortAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> RollingStartAsync(string bundleName, int framesPerSecond, int ringSeconds, bool encodeMp4, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> RollingStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> RollingStopAsync(string captureId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JsonElement> RollingAbortAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> NavigateStepAsync(NavigationState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> AimStepAsync(AimState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> CombatStepAsync(CombatState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> InteractAsync(string semanticInput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> UseAbilityAsync(string semanticInput, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
