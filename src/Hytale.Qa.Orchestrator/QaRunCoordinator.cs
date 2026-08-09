using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Orchestrator;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QaCoordinatedRunPhase
{
    Idle, Running, AwaitingHandoff, Cancelling, Completed, Failed, Blocked, Untested, InvalidEvidence,
    AbortedSafety, Faulted
}

public sealed record QaCoordinatedRunState(
    string? RunId,
    QaCoordinatedRunPhase Phase,
    string? Kind,
    string? TargetId,
    int ScenarioCursor,
    int ScenarioCount,
    string? CurrentScenarioId,
    int CompletedScenarios,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ResultPath,
    string Message);

public sealed record QaRuntimePins(
    int ClientProcessId,
    string SessionId,
    string LauncherEvidenceId,
    string LauncherEvidenceFileName,
    string LauncherEvidenceSha256,
    string WorldId,
    string ServerEndpoint,
    string ClientCapabilitySha256,
    string WorkerExecutableSha256,
    string ObserverRoot,
    string WorkerTraceRoot);

public sealed record QaScenarioRuntimePins(
    int ScenarioIndex,
    string ScenarioId,
    QaRuntimePins Pins,
    string PinsPath,
    string AuthenticationPath);

public sealed record QaCoordinatedRunResult(
    string RunId,
    string Kind,
    string TargetId,
    QaCoordinatedRunPhase Phase,
    IReadOnlyDictionary<string, string> ScenarioCanonicalSha256,
    QaRuntimePins? RuntimePins,
    IReadOnlyList<QaScenarioRuntimePins> ScenarioRuntimePins,
    IReadOnlyList<QaRunReport> Scenarios,
    bool TeardownAttempted,
    bool TeardownSucceeded,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset FinishedAtUtc,
    string EvidenceDirectory,
    string? ArtifactManifestPath,
    string? ArtifactManifestSha256,
    string? ArtifactManifestAuthenticationPath,
    string? ResultAuthenticationPath,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ArtifactGaps,
    string Code,
    string Message);

public sealed record QaArtifactManifestItem(string RelativePath, long Bytes, string Sha256);
public sealed record QaArtifactManifest(string Schema, string RunId, IReadOnlyList<string> SessionIds,
    DateTimeOffset GeneratedAtUtc, IReadOnlyList<QaArtifactManifestItem> Items);

public sealed class QaRunCoordinator : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly QaPaths paths;
    private readonly ScenarioCatalog scenarios;
    private readonly SuiteCatalog suites;
    private readonly IHytaleScenarioRuntimeFactory runtimes;
    private readonly ILauncherProofValidator launcherProofs;
    private readonly IWorkerControlService worker;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task? activeTask;
    private TaskCompletionSource<HandoffSubmission>? handoff;
    private IReadOnlyList<QaRuntimePins>? handoffUsedPins;
    private HandoffPurpose handoffPurpose;
    private QaLifecycleHandoffRequest? lifecycleRequest;
    private FileStream? lease;
    private QaCoordinatedRunState state;

    private sealed record HandoffSubmission(int ClientProcessId, string EvidenceFileName,
        LauncherProofSummary Summary, DateTimeOffset SubmittedAtUtc);
    private enum HandoffPurpose { None, FreshScenario, Lifecycle }

    private sealed class InvalidEvidenceException(string message) : Exception(message);

    public QaRunCoordinator(QaPaths paths, ScenarioCatalog scenarios, SuiteCatalog suites,
        IHytaleScenarioRuntimeFactory runtimes, ILauncherProofValidator launcherProofs,
        IWorkerControlService worker, TimeProvider? time = null)
    {
        this.paths = paths;
        this.scenarios = scenarios;
        this.suites = suites;
        this.runtimes = runtimes;
        this.launcherProofs = launcherProofs;
        this.worker = worker;
        this.time = time ?? TimeProvider.System;
        state = new(null, QaCoordinatedRunPhase.Idle, null, null, 0, 0, null, 0, null,
            this.time.GetUtcNow(), null, "No QA run is active.");
    }

    public QaCoordinatedRunState State => state;

    public Task<QaCoordinatedRunState> StartScenarioAsync(string scenarioId, int clientProcessId,
        string launcherEvidenceFileName, CancellationToken cancellationToken) =>
        StartAsync("scenario", scenarioId, [scenarioId], clientProcessId, launcherEvidenceFileName,
            failFast: true, freshSessionPerScenario: false, cancellationToken);

    public Task<QaCoordinatedRunState> StartSuiteAsync(string suiteId, int clientProcessId,
        string launcherEvidenceFileName, CancellationToken cancellationToken)
    {
        var suite = suites.Get(suiteId);
        return StartAsync("suite", suiteId, suite.ScenarioIds, clientProcessId, launcherEvidenceFileName,
            SuiteFailFast(suite), SuiteRequiresFreshSession(suite), cancellationToken);
    }

    public async Task<QaCoordinatedRunState> HandoffSuiteAsync(string runId, int clientProcessId,
        string launcherEvidenceFileName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Run id must be a non-empty UUID.", nameof(runId));
        if (clientProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(clientProcessId));
        var summary = await launcherProofs.ValidateAsync(launcherEvidenceFileName, cancellationToken)
            .ConfigureAwait(false);
        if (summary.ClientProcessId != clientProcessId)
            throw new InvalidDataException("Handoff proof client PID does not match the supplied PID.");
        if (!summary.Ready || summary.ClientProcessCount != 1)
            throw new InvalidDataException(
                "Handoff proof must be armed and attest exactly one pinned Hytale client.");

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Phase != QaCoordinatedRunPhase.AwaitingHandoff || handoffPurpose != HandoffPurpose.FreshScenario ||
                !string.Equals(state.RunId, runId, StringComparison.Ordinal) || handoff is null)
                throw new InvalidOperationException("No matching suite is awaiting a launcher proof handoff.");
            if (handoffUsedPins is null || handoffUsedPins.Any(item =>
                    item.ClientProcessId == summary.ClientProcessId ||
                    string.Equals(item.SessionId, summary.SessionId, StringComparison.Ordinal) ||
                    string.Equals(item.LauncherEvidenceId, summary.EvidenceId, StringComparison.Ordinal) ||
                    string.Equals(item.LauncherEvidenceFileName, launcherEvidenceFileName,
                        StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.LauncherEvidenceSha256, summary.LaunchEvidenceSha256,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException(
                    "Handoff is not fresh: session, evidence, proof hash, proof file, and client PID must all be new.");
            var submission = new HandoffSubmission(clientProcessId, launcherEvidenceFileName, summary,
                time.GetUtcNow());
            var acceptedState = state with
            {
                Phase = QaCoordinatedRunPhase.Running,
                UpdatedAtUtc = time.GetUtcNow(),
                Message = "Handoff proof accepted; independent revalidation is pending before input can attach."
            };
            PersistState(acceptedState);
            if (!handoff.TrySetResult(submission))
                throw new InvalidOperationException("The suite handoff slot is already satisfied.");
            state = acceptedState;
            return state;
        }
        finally { gate.Release(); }
    }

    public async Task<QaCoordinatedRunState> HandoffRunAsync(string runId, int clientProcessId,
        string launcherEvidenceFileName, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(runId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Run id must be a non-empty UUID.", nameof(runId));
        if (clientProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(clientProcessId));
        var summary = await launcherProofs.ValidateAsync(launcherEvidenceFileName, cancellationToken)
            .ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = lifecycleRequest;
            if (state.Phase != QaCoordinatedRunPhase.AwaitingHandoff ||
                !string.Equals(state.RunId, runId, StringComparison.Ordinal) || handoff is null ||
                handoffPurpose != HandoffPurpose.Lifecycle || request is null)
                throw new InvalidOperationException("No matching scenario lifecycle step is awaiting a proof handoff.");
            if (!summary.Ready || summary.ClientProcessCount != 1 || summary.ClientProcessId != clientProcessId ||
                clientProcessId != request.ClientProcessId)
                throw new InvalidDataException("Lifecycle handoff must attest the same single pinned Hytale client PID.");
            if (!string.Equals(summary.WorldId, request.WorldId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(summary.EvidenceId, request.PriorEvidenceId, StringComparison.Ordinal) ||
                string.Equals(launcherEvidenceFileName, request.PriorEvidenceFileName,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Lifecycle handoff must preserve the world and rotate proof file/evidence identity.");
            if (request.Operation == "server.restart" && summary.ServerProcessId == request.ServerProcessId)
                throw new InvalidDataException("Server restart handoff did not prove a new world-server PID.");
            var submission = new HandoffSubmission(clientProcessId, launcherEvidenceFileName, summary,
                time.GetUtcNow());
            var accepted = state with
            {
                Phase = QaCoordinatedRunPhase.Running,
                UpdatedAtUtc = time.GetUtcNow(),
                Message = "Lifecycle proof accepted; the runtime is independently checking client/world continuity before rearming input."
            };
            PersistState(accepted);
            if (!handoff.TrySetResult(submission))
                throw new InvalidOperationException("The lifecycle handoff slot is already satisfied.");
            state = accepted;
            return state;
        }
        finally { gate.Release(); }
    }

    public async Task<QaCoordinatedRunState> CancelAsync(string runId, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Phase is not QaCoordinatedRunPhase.Running and
                not QaCoordinatedRunPhase.AwaitingHandoff and not QaCoordinatedRunPhase.Cancelling ||
                !string.Equals(state.RunId, runId, StringComparison.Ordinal))
                throw new InvalidOperationException("No matching active run exists.");
            state = state with { Phase = QaCoordinatedRunPhase.Cancelling, UpdatedAtUtc = time.GetUtcNow(), Message = "Cancellation requested; teardown is running." };
            PersistState(state);
            cancellation?.Cancel();
            handoff?.TrySetCanceled(cancellation?.Token ?? new CancellationToken(canceled: true));
            return state;
        }
        finally { gate.Release(); }
    }

    public QaCoordinatedRunResult Result(string runId)
    {
        if (!Guid.TryParse(runId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Run id must be a non-empty UUID.", nameof(runId));
        var path = ResultPath(runId);
        if (!File.Exists(path)) throw new KeyNotFoundException($"No completed result exists for run '{runId}'.");
        var result = JsonSerializer.Deserialize<QaCoordinatedRunResult>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new InvalidDataException("Durable run result is empty.");
        var finalPins = result.ScenarioRuntimePins?.LastOrDefault();
        var resultSeal = VerifySealedFile(result.ResultAuthenticationPath, path, "run-result");
        if (finalPins is not null) RequireSealIdentity(resultSeal, finalPins.Pins);
        if (result.ArtifactManifestPath is not null)
        {
            var manifestSeal = VerifySealedFile(result.ArtifactManifestAuthenticationPath,
                result.ArtifactManifestPath, "artifact-manifest");
            if (finalPins is not null) RequireSealIdentity(manifestSeal, finalPins.Pins);
        }
        foreach (var scenarioPins in result.ScenarioRuntimePins ?? [])
        {
            var pinSeal = VerifySealedFile(scenarioPins.AuthenticationPath, scenarioPins.PinsPath,
                "scenario-runtime-pins");
            RequireSealIdentity(pinSeal, scenarioPins.Pins);
        }
        return result;
    }

    private async Task<QaCoordinatedRunState> StartAsync(string kind, string targetId,
        IReadOnlyList<string> scenarioIds, int clientProcessId, string launcherEvidenceFileName,
        bool failFast, bool freshSessionPerScenario, CancellationToken requestCancellation)
    {
        if (clientProcessId <= 0) throw new ArgumentOutOfRangeException(nameof(clientProcessId));
        if (scenarioIds.Count == 0) throw new InvalidOperationException("Run has no scenarios.");
        foreach (var id in scenarioIds) _ = Scenario(id);
        requestCancellation.ThrowIfCancellationRequested();
        await gate.WaitAsync(requestCancellation).ConfigureAwait(false);
        var acquiredHere = false;
        try
        {
            if (state.Phase is QaCoordinatedRunPhase.Running or QaCoordinatedRunPhase.AwaitingHandoff or
                QaCoordinatedRunPhase.Cancelling)
                throw new InvalidOperationException("Another QA run already owns the serial run lease.");
            var runId = Guid.NewGuid().ToString("D");
            AcquireLease(runId);
            acquiredHere = true;
            cancellation = new CancellationTokenSource();
            var started = time.GetUtcNow();
            state = new(runId, QaCoordinatedRunPhase.Running, kind, targetId, 0, scenarioIds.Count,
                null, 0, started, started, null, "Run lease acquired; launcher proof validation is pending.");
            PersistState(state);
            activeTask = Task.Run(() => ExecuteAsync(runId, kind, targetId, scenarioIds,
                clientProcessId, launcherEvidenceFileName, failFast, freshSessionPerScenario, started,
                cancellation.Token));
            return state;
        }
        catch
        {
            if (acquiredHere) ReleaseLease();
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task ExecuteAsync(string runId, string kind, string targetId,
        IReadOnlyList<string> scenarioIds, int clientProcessId, string launcherEvidenceFileName,
        bool failFast, bool freshSessionPerScenario, DateTimeOffset started,
        CancellationToken cancellationToken)
    {
        var reports = new List<QaRunReport>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        var artifactGaps = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        QaRuntimePins? pins = null;
        var scenarioPins = new List<QaScenarioRuntimePins>();
        var teardownAttempted = false;
        var teardownSucceeded = false;
        var phase = QaCoordinatedRunPhase.Completed;
        var code = "completed";
        var message = "Serial QA run completed.";
        var runRoot = RunRoot(runId);
        Directory.CreateDirectory(runRoot);
        paths.RequireSafeEvidencePath(runRoot);
        try
        {
            HandoffSubmission submission = new(clientProcessId, launcherEvidenceFileName, null!, started);

            for (var index = 0; index < scenarioIds.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scenario = Scenario(scenarioIds[index]);
                if (index > 0)
                {
                    if (!freshSessionPerScenario)
                        throw new InvalidEvidenceException(
                            "suite-fresh-session-contract-missing: multiple scenarios cannot reuse one launcher proof.");
                    submission = await WaitForHandoffAsync(runId, scenario.Id, index, scenarioIds.Count,
                        scenarioPins.Select(item => item.Pins).ToArray(), cancellationToken)
                        .ConfigureAwait(false);
                }
                var currentPins = await ValidateHandoffAsync(submission, scenarioPins, index > 0,
                    cancellationToken).ConfigureAwait(false);
                var pinPath = Path.Combine(runRoot,
                    $"runtime-pins-{index:D3}-{SafeName(scenario.Id)}.json");
                AtomicWrite(pinPath, currentPins);
                var pinAuthenticationPath = pinPath + ".auth.json";
                AtomicWrite(pinAuthenticationPath, launcherProofs.Seal(
                    currentPins.LauncherEvidenceFileName, "scenario-runtime-pins", HashIfPresent(pinPath)));
                var scenarioPin = new QaScenarioRuntimePins(index, scenario.Id, currentPins, pinPath,
                    pinAuthenticationPath);
                scenarioPins.Add(scenarioPin);
                pins ??= currentPins;
                if (index == 0)
                    AtomicWrite(Path.Combine(runRoot, "runtime-pins.json"), currentPins);
                hashes[scenario.Id] = scenario.CanonicalSha256;
                state = state with { ScenarioCursor = index, CurrentScenarioId = scenario.Id,
                    UpdatedAtUtc = time.GetUtcNow(), Message = $"Running scenario {index + 1} of {scenarioIds.Count}." };
                PersistState(state);
                var runtime = runtimes.Create(scenario, currentPins.ClientProcessId,
                    currentPins.LauncherEvidenceFileName, runRoot);
                if (runtime is IQaLifecycleHandoffRuntime lifecycle)
                    lifecycle.ConfigureLifecycleHandoff((request, token) =>
                        WaitForLifecycleHandoffAsync(runId, scenario.Id, index, scenarioIds.Count,
                            request, token));
                var report = await new ScenarioExecutor(runtime, time).RunAsync(scenario, cancellationToken).ConfigureAwait(false);
                reports.Add(report);
                AtomicWrite(Path.Combine(runRoot, $"scenario-{index:D3}-{SafeName(scenario.Id)}.json"), report);
                if (freshSessionPerScenario && index + 1 < scenarioIds.Count &&
                    worker.State.Phase != WorkerControlPhase.Detached)
                {
                    if (worker.State.Phase is WorkerControlPhase.Armed or WorkerControlPhase.Faulted)
                        _ = await worker.DetachAsync(CancellationToken.None).ConfigureAwait(false);
                    if (worker.State.Phase != WorkerControlPhase.Detached)
                        throw new InvalidEvidenceException(
                            "handoff-worker-not-detached: prior scenario input lease was not fully released.");
                }
                if (currentPins is not null)
                {
                    var gaps = AuditDeclaredArtifacts(scenario, currentPins.WorkerTraceRoot, runRoot, runId);
                    if (gaps.Count > 0)
                    {
                        artifactGaps[scenario.Id] = gaps;
                        if (report.Passed)
                        {
                            phase = QaCoordinatedRunPhase.Untested;
                            code = "required-artifacts-missing";
                            message = $"Scenario '{scenario.Id}' completed its steps but lacks required evidence: {string.Join(", ", gaps)}.";
                        }
                    }
                }
                state = state with { CompletedScenarios = reports.Count, UpdatedAtUtc = time.GetUtcNow(),
                    Message = $"Scenario '{scenario.Id}' completed as {report.Outcome}." };
                PersistState(state);
                if (!report.Passed)
                {
                    phase = CoordinatedPhase(report.Outcome);
                    code = $"scenario-{report.Outcome.ToString().ToLowerInvariant()}";
                    message = $"Scenario '{scenario.Id}' completed as {report.Outcome}; the QA run did not pass.";
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (failFast && !report.Passed) break;
            }
        }
        catch (OperationCanceledException)
        {
            phase = QaCoordinatedRunPhase.Blocked;
            code = "run-cancelled";
            message = "Run was cancelled; completed evidence was preserved.";
        }
        catch (InvalidEvidenceException failure)
        {
            phase = QaCoordinatedRunPhase.InvalidEvidence;
            code = "handoff-invalid-evidence";
            message = failure.Message;
        }
        catch (Exception failure)
        {
            phase = QaCoordinatedRunPhase.Faulted;
            code = "coordinator-fault";
            message = failure.Message;
        }
        finally
        {
            teardownAttempted = true;
            try
            {
                if (worker.State.Phase is WorkerControlPhase.Armed or WorkerControlPhase.Faulted)
                    await worker.DetachAsync(CancellationToken.None).ConfigureAwait(false);
                teardownSucceeded = worker.State.Phase == WorkerControlPhase.Detached;
            }
            catch (Exception failure)
            {
                teardownSucceeded = false;
                phase = QaCoordinatedRunPhase.Faulted;
                code = "coordinator-teardown-failed";
                message = failure.Message;
            }

            var finished = time.GetUtcNow();
            string? artifactManifestPath = null;
            string? artifactManifestSha256 = null;
            string? artifactManifestAuthenticationPath = null;
            if (scenarioPins.Count > 0)
            {
                var manifest = BuildArtifactManifest(runId, scenarioPins, runRoot, started);
                artifactManifestPath = Path.Combine(runRoot, "artifact-manifest.json");
                AtomicWrite(artifactManifestPath, manifest);
                artifactManifestSha256 = HashIfPresent(artifactManifestPath);
                artifactManifestAuthenticationPath = artifactManifestPath + ".auth.json";
                AtomicWrite(artifactManifestAuthenticationPath,
                    launcherProofs.Seal(scenarioPins[^1].Pins.LauncherEvidenceFileName,
                        "artifact-manifest", artifactManifestSha256));
            }
            var resultPath = ResultPath(runId);
            // A structurally valid launcher proof can authenticate the coordinator's
            // preflight failure even when freshness validation fails before runtime
            // pins are created. This keeps fault reporting tamper-evident instead of
            // leaving a result.json that the result reader cannot consume.
            var resultAuthenticationPath = resultPath + ".auth.json";
            var result = new QaCoordinatedRunResult(runId, kind, targetId, phase, hashes, pins,
                scenarioPins, reports, teardownAttempted, teardownSucceeded, started, finished, runRoot,
                artifactManifestPath, artifactManifestSha256, artifactManifestAuthenticationPath,
                resultAuthenticationPath, artifactGaps, code, message);
            AtomicWrite(resultPath, result);
            var resultEvidenceFile = scenarioPins.Count > 0
                ? scenarioPins[^1].Pins.LauncherEvidenceFileName
                : launcherEvidenceFileName;
            AtomicWrite(resultAuthenticationPath,
                launcherProofs.Seal(resultEvidenceFile,
                    "run-result", HashIfPresent(resultPath)));
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                state = state with { Phase = phase, CurrentScenarioId = null, UpdatedAtUtc = finished,
                    ResultPath = resultPath, Message = message };
                PersistState(state);
                handoff = null;
                ReleaseLease();
            }
            finally { gate.Release(); }
        }
    }

    private async Task<HandoffSubmission> WaitForHandoffAsync(string runId, string scenarioId,
        int scenarioIndex, int scenarioCount, IReadOnlyList<QaRuntimePins> usedPins,
        CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<HandoffSubmission>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(state.RunId, runId, StringComparison.Ordinal) ||
                state.Phase != QaCoordinatedRunPhase.Running || handoff is not null)
                throw new InvalidOperationException("Suite cannot enter a new proof handoff from its current state.");
            handoff = pending;
            handoffUsedPins = usedPins;
            handoffPurpose = HandoffPurpose.FreshScenario;
            state = state with
            {
                Phase = QaCoordinatedRunPhase.AwaitingHandoff,
                ScenarioCursor = scenarioIndex,
                CurrentScenarioId = scenarioId,
                UpdatedAtUtc = time.GetUtcNow(),
                Message = $"Scenario {scenarioIndex + 1} of {scenarioCount} requires a fresh launcher-owned OFFLINE session. " +
                    "Close and archive the prior session normally, create and arm a new one through the official UI, then submit its new PID and proof."
            };
            PersistState(state);
        }
        finally { gate.Release(); }

        try { return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(handoff, pending))
                {
                    handoff = null;
                    handoffUsedPins = null;
                    handoffPurpose = HandoffPurpose.None;
                }
            }
            finally { gate.Release(); }
        }
    }

    private async Task<QaLifecycleHandoffSubmission> WaitForLifecycleHandoffAsync(
        string runId,
        string scenarioId,
        int scenarioIndex,
        int scenarioCount,
        QaLifecycleHandoffRequest request,
        CancellationToken cancellationToken)
    {
        var pending = new TaskCompletionSource<HandoffSubmission>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.Equals(state.RunId, runId, StringComparison.Ordinal) ||
                state.Phase != QaCoordinatedRunPhase.Running || handoff is not null ||
                worker.State.Phase != WorkerControlPhase.Detached)
                throw new InvalidOperationException(
                    "Lifecycle handoff requires the matching running scenario and a fully detached input worker.");
            handoff = pending;
            handoffPurpose = HandoffPurpose.Lifecycle;
            lifecycleRequest = request;
            state = state with
            {
                Phase = QaCoordinatedRunPhase.AwaitingHandoff,
                ScenarioCursor = scenarioIndex,
                CurrentScenarioId = scenarioId,
                UpdatedAtUtc = time.GetUtcNow(),
                Message = request.Operation == "server.restart"
                    ? $"Scenario {scenarioIndex + 1} of {scenarioCount} is paused at server.restart. Restart the same launcher-owned OFFLINE world while preserving the one client, create a renewed proof, then call qa_run_handoff."
                    : $"Scenario {scenarioIndex + 1} of {scenarioCount} is paused at client.reconnect. Reconnect the same client to the same launcher-owned OFFLINE world, create a renewed proof, then call qa_run_handoff."
            };
            PersistState(state);
        }
        finally { gate.Release(); }

        try
        {
            var accepted = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(accepted.ClientProcessId, accepted.EvidenceFileName, accepted.SubmittedAtUtc);
        }
        finally
        {
            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(handoff, pending))
                {
                    handoff = null;
                    handoffUsedPins = null;
                    handoffPurpose = HandoffPurpose.None;
                    lifecycleRequest = null;
                }
            }
            finally { gate.Release(); }
        }
    }

    private async Task<QaRuntimePins> ValidateHandoffAsync(HandoffSubmission submission,
        IReadOnlyList<QaScenarioRuntimePins> previous, bool requireFresh,
        CancellationToken cancellationToken)
    {
        var summary = await launcherProofs.ValidateAsync(submission.EvidenceFileName, cancellationToken)
            .ConfigureAwait(false);
        if (summary.ClientProcessId != submission.ClientProcessId)
            throw new InvalidEvidenceException(
                "handoff-client-pid-mismatch: launcher evidence does not bind the submitted client PID.");
        if (requireFresh && !SameProofBoundary(summary, submission.Summary))
            throw new InvalidEvidenceException(
                "handoff-proof-changed-during-acceptance: launcher evidence identity changed between submission and execution.");
        if (!summary.Ready || summary.ClientProcessCount != 1)
            throw new InvalidEvidenceException("handoff-proof-not-ready: launcher evidence is not armed.");
        if (requireFresh && previous.Any(item =>
                item.Pins.ClientProcessId == summary.ClientProcessId ||
                string.Equals(item.Pins.SessionId, summary.SessionId, StringComparison.Ordinal) ||
                string.Equals(item.Pins.LauncherEvidenceId, summary.EvidenceId, StringComparison.Ordinal) ||
                string.Equals(item.Pins.LauncherEvidenceFileName, submission.EvidenceFileName,
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Pins.LauncherEvidenceSha256, summary.LaunchEvidenceSha256,
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidEvidenceException(
                "handoff-proof-not-fresh: session, evidence, proof hash, proof file, and client PID must all be new.");
        return new(summary.ClientProcessId, summary.SessionId, summary.EvidenceId,
            submission.EvidenceFileName, summary.LaunchEvidenceSha256, summary.WorldId,
            summary.ServerEndpoint, HashIfPresent(paths.ClientCapabilityPath),
            HashIfPresent(paths.WorkerExecutablePath), paths.LauncherObserverSpoolDirectory,
            paths.SessionArtifactDirectory(summary.SessionId));
    }

    private static bool SameProofBoundary(LauncherProofSummary left, LauncherProofSummary right) =>
        string.Equals(left.EvidenceId, right.EvidenceId, StringComparison.Ordinal) &&
        string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
        string.Equals(left.WorldId, right.WorldId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ServerEndpoint, right.ServerEndpoint, StringComparison.OrdinalIgnoreCase) &&
        left.LauncherProcessId == right.LauncherProcessId &&
        left.ClientProcessId == right.ClientProcessId &&
        left.ServerProcessId == right.ServerProcessId &&
        string.Equals(left.LaunchEvidenceSha256, right.LaunchEvidenceSha256,
            StringComparison.OrdinalIgnoreCase) && left.ClientProcessCount == right.ClientProcessCount &&
        left.Ready == right.Ready;

    private QaScenario Scenario(string id)
    {
        var summary = scenarios.List().SingleOrDefault(value => string.Equals(value.Id, id, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Unknown QA scenario '{id}'.");
        var path = Path.GetFullPath(Path.Combine(paths.ProjectRoot, summary.Path));
        return QaScenarioLoader.Load(path);
    }

    private static QaCoordinatedRunPhase CoordinatedPhase(QaRunOutcome outcome) => outcome switch
    {
        QaRunOutcome.Passed => QaCoordinatedRunPhase.Completed,
        QaRunOutcome.Failed => QaCoordinatedRunPhase.Failed,
        QaRunOutcome.Blocked => QaCoordinatedRunPhase.Blocked,
        QaRunOutcome.Untested => QaCoordinatedRunPhase.Untested,
        QaRunOutcome.InvalidEvidence => QaCoordinatedRunPhase.InvalidEvidence,
        QaRunOutcome.AbortedSafety => QaCoordinatedRunPhase.AbortedSafety,
        _ => QaCoordinatedRunPhase.Faulted
    };

    private bool SuiteFailFast(QaSuiteSummary suite)
    {
        var path = Path.GetFullPath(Path.Combine(paths.ProjectRoot, suite.Path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("execution").GetProperty("failFast").GetBoolean();
    }

    private bool SuiteRequiresFreshSession(QaSuiteSummary suite)
    {
        var path = Path.GetFullPath(Path.Combine(paths.ProjectRoot, suite.Path));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("execution").GetProperty("freshSessionPerScenario").GetBoolean();
    }

    private void AcquireLease(string runId)
    {
        Directory.CreateDirectory(paths.RunDirectory);
        paths.RequireSafeEvidencePath(paths.RunDirectory);
        var leasePath = Path.Combine(paths.RunDirectory, "active-run.lease.json");
        if (File.Exists(leasePath))
        {
            if (LeaseOwnerAlive(leasePath)) throw new InvalidOperationException("A durable QA run lease is owned by another live coordinator.");
            File.Delete(leasePath);
        }
        lease = new FileStream(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
            4096, FileOptions.WriteThrough);
        var owner = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "hytale-qa-run-lease-v1", runId, processId = Environment.ProcessId,
            processStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(), acquiredAtUtc = time.GetUtcNow()
        }, JsonOptions);
        lease.Write(owner);
        lease.Flush(flushToDisk: true);
    }

    private static bool LeaseOwnerAlive(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var pid = root.GetProperty("processId").GetInt32();
            var started = root.GetProperty("processStartedAtUtc").GetDateTimeOffset();
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime() == started;
        }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or
                                            System.ComponentModel.Win32Exception or JsonException or IOException)
        { return false; }
    }

    private void ReleaseLease()
    {
        var path = lease?.Name;
        lease?.Dispose();
        lease = null;
        if (path is not null) try { File.Delete(path); } catch (IOException) { }
        cancellation?.Dispose();
        cancellation = null;
        handoff = null;
        handoffUsedPins = null;
        handoffPurpose = HandoffPurpose.None;
        lifecycleRequest = null;
    }

    private void PersistState(QaCoordinatedRunState value)
    {
        if (value.RunId is null) return;
        var root = RunRoot(value.RunId);
        Directory.CreateDirectory(root);
        AtomicWrite(Path.Combine(root, "state.json"), value);
    }

    private string RunRoot(string runId) => Path.Combine(paths.RunDirectory, runId);
    private string ResultPath(string runId) => Path.Combine(RunRoot(runId), "result.json");
    private static string SafeName(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
    private static string HashIfPresent(string path)
    {
        if (!File.Exists(path)) return "MISSING";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private LauncherEvidenceSeal VerifySealedFile(string? authenticationPath, string path, string purpose)
    {
        if (authenticationPath is null || !File.Exists(authenticationPath))
            throw new InvalidDataException($"Authenticated {purpose} sidecar is missing.");
        var seal = JsonSerializer.Deserialize<LauncherEvidenceSeal>(File.ReadAllBytes(authenticationPath), JsonOptions)
            ?? throw new InvalidDataException($"Authenticated {purpose} sidecar is empty.");
        launcherProofs.VerifySeal(seal, purpose, HashIfPresent(path));
        return seal;
    }

    private static void RequireSealIdentity(LauncherEvidenceSeal seal, QaRuntimePins pins)
    {
        if (!string.Equals(seal.SessionId, pins.SessionId, StringComparison.Ordinal) ||
            !string.Equals(seal.EvidenceId, pins.LauncherEvidenceId, StringComparison.Ordinal))
            throw new InvalidDataException("Authenticated evidence seal does not match its scenario runtime pins.");
    }

    private IReadOnlyList<string> AuditDeclaredArtifacts(QaScenario scenario, string workerRoot, string runRoot, string runId)
    {
        paths.RequireSafeEvidenceTree(workerRoot);
        paths.RequireSafeEvidenceTree(runRoot);
        var gaps = new List<string>();
        var files = CommittedFiles(workerRoot).Concat(CommittedFiles(runRoot)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var prefix = SafeName(runId) + "-" + SafeName(scenario.Id);
        var screenshots = files.Count(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetExtension(path), ".bmp", StringComparison.OrdinalIgnoreCase));
        var screenshotMode = scenario.Artifacts.TryGetProperty("screenshots", out var mode) ? mode.GetString() : "none";
        var requiredScreenshots = string.Equals(screenshotMode, "all_actions", StringComparison.Ordinal)
            ? scenario.Steps.Count(step => step.Operation is not "session.start" and not "session.stop")
            : scenario.Steps.Count(step => step.Operation.StartsWith("assert.", StringComparison.Ordinal));
        if (!string.Equals(screenshotMode, "none", StringComparison.Ordinal) && screenshots < Math.Max(1, requiredScreenshots))
            gaps.Add($"screenshots:{screenshotMode}");
        if (scenario.Artifacts.TryGetProperty("rollingVideoSeconds", out var rolling) && rolling.GetInt32() > 0 &&
            !files.Any(path => path.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) &&
                path.Contains(SafeName(runId), StringComparison.OrdinalIgnoreCase) &&
                path.Contains(SafeName(scenario.Id), StringComparison.OrdinalIgnoreCase)))
            gaps.Add("rollingVideoSeconds");
        if (scenario.Artifacts.TryGetProperty("audio", out var audio) && audio.GetBoolean() &&
            !files.Any(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase)))
            gaps.Add("audio");
        if (scenario.Artifacts.TryGetProperty("inputTranscript", out var transcript) && transcript.GetBoolean() &&
            !files.Any(path => string.Equals(Path.GetExtension(path), ".ndjson", StringComparison.OrdinalIgnoreCase)))
            gaps.Add("inputTranscript");
        if (scenario.Artifacts.TryGetProperty("packetEvidence", out var packets) && packets.GetBoolean() &&
            !files.Any(path => Path.GetFileName(path).StartsWith(prefix + "-packet-evidence-", StringComparison.OrdinalIgnoreCase) &&
                ValidEvidenceFile(path, "hytale-qa-packet-evidence-v1", requireEvidenceValid: true)))
            gaps.Add("packetEvidence");
        if (scenario.Artifacts.TryGetProperty("stateDiffs", out var stateDiffs) && stateDiffs.GetBoolean() &&
            !files.Any(path => Path.GetFileName(path).StartsWith(prefix + "-state-diff-", StringComparison.OrdinalIgnoreCase) &&
                ValidEvidenceFile(path, "hytale-qa-state-diff-v1", requireEvidenceValid: false)))
            gaps.Add("stateDiffs");
        return gaps;
    }

    private QaArtifactManifest BuildArtifactManifest(string runId,
        IReadOnlyList<QaScenarioRuntimePins> scenarioPins, string runRoot, DateTimeOffset started)
    {
        foreach (var pin in scenarioPins) paths.RequireSafeEvidenceTree(pin.Pins.WorkerTraceRoot);
        paths.RequireSafeEvidenceTree(runRoot);
        var sessionItems = scenarioPins.SelectMany(pin => CommittedFiles(pin.Pins.WorkerTraceRoot)
            .Select(path => (Path: path, Relative:
                $"session-{pin.ScenarioIndex:D3}-{pin.Pins.SessionId}/" +
                Path.GetRelativePath(pin.Pins.WorkerTraceRoot, path).Replace('\\', '/'))));
        var items = sessionItems
            .Concat(CommittedFiles(runRoot)
                .Where(path => !string.Equals(Path.GetFileName(path), "artifact-manifest.json", StringComparison.OrdinalIgnoreCase) &&
                               !string.Equals(Path.GetFileName(path), "result.json", StringComparison.OrdinalIgnoreCase) &&
                               !path.EndsWith(".auth.json", StringComparison.OrdinalIgnoreCase))
                .Select(path => (Path: path, Relative: "run/" + Path.GetRelativePath(runRoot, path).Replace('\\', '/'))))
            .Where(item => File.GetLastWriteTimeUtc(item.Path) >= started.UtcDateTime.AddSeconds(-1))
            .OrderBy(item => item.Relative, StringComparer.Ordinal)
            .Select(item => new QaArtifactManifestItem(item.Relative,
                new FileInfo(item.Path).Length, HashIfPresent(item.Path))).ToArray();
        return new("hytale-qa-artifact-manifest-v2", runId,
            scenarioPins.Select(item => item.Pins.SessionId).ToArray(), time.GetUtcNow(), items);
    }

    private static IEnumerable<string> CommittedFiles(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => IsCommittedArtifact(root, path))
        : [];

    private static bool ValidEvidenceFile(string path, string schema, bool requireEvidenceValid)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            return root.TryGetProperty("schema", out var schemaValue) &&
                   string.Equals(schemaValue.GetString(), schema, StringComparison.Ordinal) &&
                   root.TryGetProperty("observationSequence", out var sequence) && sequence.TryGetInt64(out var number) && number >= 0 &&
                   (!requireEvidenceValid || root.TryGetProperty("evidenceValid", out var valid) && valid.GetBoolean());
        }
        catch (Exception failure) when (failure is IOException or JsonException or InvalidOperationException)
        { return false; }
    }

    private static bool IsCommittedArtifact(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith(".", StringComparison.Ordinal) ||
                segment.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Durable path has no directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation?.Cancel();
        handoff?.TrySetCanceled();
        if (activeTask is not null) try { await activeTask.ConfigureAwait(false); } catch { }
        ReleaseLease();
        gate.Dispose();
        GC.SuppressFinalize(this);
    }
}
