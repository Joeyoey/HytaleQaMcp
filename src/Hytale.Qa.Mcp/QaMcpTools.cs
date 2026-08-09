using System.ComponentModel;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;
using ModelContextProtocol.Server;

namespace Hytale.Qa.Mcp;

[McpServerToolType]
public sealed class QaMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [McpServerTool(Name = "qa_session_start"), Description("Starts and proves the dedicated offline QA server. Input stays disarmed unless every offline/client/observer predicate passes.")]
    public static async Task<string> StartSession(QaSessionOrchestrator sessions, CancellationToken cancellationToken) =>
        Json(await sessions.StartAsync(cancellationToken));

    [McpServerTool(Name = "qa_session_state"), Description("Returns the current immutable QA evidence mode and offline/session arming state without changing anything.")]
    public static string SessionState(QaSessionOrchestrator sessions) => Json(sessions.Status);

    [McpServerTool(Name = "qa_session_refresh"), Description("Revalidates the running server's effective offline proof. Loss of proof disarms the session.")]
    public static async Task<string> RefreshSession(QaSessionOrchestrator sessions, CancellationToken cancellationToken) =>
        Json(await sessions.RefreshAsync(cancellationToken));

    [McpServerTool(Name = "qa_observe_player"), Description("Returns a read-only, nonce-authenticated offline server observation for the fixture player. This never mutates game state.")]
    public static async Task<string> ObservePlayer(
        QaSessionOrchestrator sessions,
        [Description("Fixture player UUID from the selected scenario.")] string playerId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(playerId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("playerId must be a non-empty UUID.", nameof(playerId));
        return Json(await sessions.ObserveAsync(parsed, cancellationToken));
    }

    [McpServerTool(Name = "qa_observe_sole_player"), Description("Returns the raw, read-only, nonce-authenticated sole-player observation for the currently attached launcher-owned OFFLINE session. No caller-supplied player id or game state is accepted.")]
    public static async Task<string> ObserveSolePlayer(
        IWorkerControlService worker,
        CancellationToken cancellationToken) =>
        Json(await worker.ObserveSolePlayerAsync(cancellationToken));

    [McpServerTool(Name = "qa_session_stop"), Description("Stops only the label-verified offline QA deployment and preserves its isolated data.")]
    public static async Task<string> StopSession(QaSessionOrchestrator sessions, CancellationToken cancellationToken) =>
        Json(await sessions.StopAsync(cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_bootstrap"), Description("Bootstraps the official launcher-owned QA process environment. This does not prove or force OFFLINE mode; physical input remains disarmed until qa_launcher_offline_arm verifies the actual process/world boundary.")]
    public static async Task<string> LauncherBootstrap(LauncherQaSessionController launcher,
        [Description("Optional canonical session UUID; omit to create one.")] string? sessionId,
        CancellationToken cancellationToken) => Json(await launcher.BootstrapAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_arm"), Description("Arms launcher-owned testing only after the actual server reports exact OFFLINE mode and the authenticated process/world proof is complete. It cannot convert an online session to offline.")]
    public static async Task<string> LauncherArm(LauncherQaSessionController launcher, string sessionId,
        CancellationToken cancellationToken) => Json(await launcher.ArmAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_state"), Description("Reads launcher-owned QA lifecycle state without arming input or changing the game.")]
    public static async Task<string> LauncherState(LauncherQaSessionController launcher, string sessionId,
        CancellationToken cancellationToken) => Json(await launcher.StateAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_verify"), Description("Revalidates the exact OFFLINE, process-tree, loopback, observer, world, and artifact proof without sending game input.")]
    public static async Task<string> LauncherVerify(LauncherQaSessionController launcher, string sessionId,
        CancellationToken cancellationToken) => Json(await launcher.VerifyAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_stop"), Description("Finalizes the matching launcher-owned QA lifecycle and releases QA state only after the game and launcher have already been closed by the user or agent. It does not terminate either process.")]
    public static async Task<string> LauncherStop(LauncherQaSessionController launcher, string sessionId,
        CancellationToken cancellationToken) => Json(await launcher.StopAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_offline_clear"), Description("Clears only a stopped matching launcher-owned QA session record after lifecycle verification.")]
    public static async Task<string> LauncherClear(LauncherQaSessionController launcher, string sessionId,
        CancellationToken cancellationToken) => Json(await launcher.ClearAsync(sessionId, cancellationToken));

    [McpServerTool(Name = "qa_launcher_proof_validate"), Description("Read-only validation of a launcher-owned singleplayer proof record, exact world/process/file pins, loopback boundary, and fresh authenticated observer. Accepts only a plain file name in the isolated launcher proof directory.")]
    public static async Task<string> ValidateLauncherProof(
        LauncherSingleplayerProofProviderFactory launcherProofs,
        [Description("Plain .json evidence file name in run/qa-offline/launcher-singleplayer-control/proofs.")] string evidenceFileName,
        CancellationToken cancellationToken) =>
        Json(await launcherProofs.ValidateAsync(evidenceFileName, cancellationToken));

    [McpServerTool(Name = "qa_client_attach_launcher_singleplayer"), Description("Attaches to exactly one already-running Hytale client only after strict launcher-owned offline singleplayer proof. It never launches Hytale, reads tokens, or weakens the Docker predicate.")]
    public static async Task<string> AttachLauncherSingleplayer(
        IWorkerControlService worker,
        [Description("PID of the single already-running HytaleClient.exe; it must exactly match the signed launch evidence.")] int processId,
        [Description("Plain .json evidence file name in run/qa-offline/launcher-singleplayer-control/proofs.")] string evidenceFileName,
        [Description("Enable bounded physical combat reflex for system/drop QA; it never injects damage.")] bool combatReflex,
        CancellationToken cancellationToken)
    {
        var capabilities = new EvidenceCapabilities(true, true, combatReflex, false, false, false);
        return Json(await worker.AttachLauncherAsync(processId, evidenceFileName, AssistanceMode.GuidedPhysical,
            capabilities, cancellationToken));
    }

    [McpServerTool(Name = "qa_client_state"), Description("Returns the worker lease, heartbeat, and fail-closed state without changing input.")]
    public static string ClientState(QaSessionOrchestrator sessions, IWorkerControlService worker) =>
        Json(new { session = sessions.Status, worker = worker.State });

    [McpServerTool(Name = "qa_client_detach"), Description("Releases held input and audio, terminates the worker process, and leaves the isolated server running.")]
    public static async Task<string> DetachClient(QaSessionOrchestrator sessions, CancellationToken cancellationToken) =>
        Json(await sessions.DetachClientAsync(cancellationToken));

    [McpServerTool(Name = "qa_client_focus"), Description("Requests focus for the pinned client HWND. Input remains blocked until independent foreground validation passes.")]
    public static async Task<string> FocusClient(IWorkerControlService worker, CancellationToken cancellationToken) =>
        Json(new { focused = await worker.FocusAsync(cancellationToken) });

    [McpServerTool(Name = "qa_client_safety"), Description("Revalidates the pinned PID, creation time, path, SHA-256, HWND, one-client count, and optional foreground state.")]
    public static async Task<string> ClientSafety(
        IWorkerControlService worker,
        [Description("Require the leased HWND to be the foreground window.")] bool requireForeground,
        CancellationToken cancellationToken) => Json(await worker.SafetyAsync(requireForeground, cancellationToken));

    [McpServerTool(Name = "qa_capture_screenshot"), Description("Captures the pinned client into the isolated artifact directory. Only a plain .bmp file name is accepted.")]
    public static async Task<string> Screenshot(
        IWorkerControlService worker,
        [Description("Plain BMP file name, without directories.")] string fileName,
        CancellationToken cancellationToken) => Json(await worker.ScreenshotAsync(fileName, cancellationToken));

    [McpServerTool(Name = "qa_audio_start"), Description("Starts process-isolated WAV capture for the pinned client process tree; unrelated system audio is excluded.")]
    public static async Task<string> AudioStart(
        IWorkerControlService worker,
        [Description("Plain WAV file name, without directories.")] string fileName,
        CancellationToken cancellationToken) => Json(await worker.AudioStartAsync(fileName, cancellationToken));

    [McpServerTool(Name = "qa_audio_state"), Description("Returns the current process-loopback audio capture state.")]
    public static async Task<string> AudioState(IWorkerControlService worker, CancellationToken cancellationToken) =>
        Json(await worker.AudioStateAsync(cancellationToken));

    [McpServerTool(Name = "qa_audio_stop"), Description("Finalizes the matching process-loopback capture and returns its WAV artifact and SHA-256.")]
    public static async Task<string> AudioStop(
        IWorkerControlService worker,
        [Description("Capture id returned by qa_audio_start.")] string captureId,
        CancellationToken cancellationToken) => Json(await worker.AudioStopAsync(captureId, cancellationToken));

    [McpServerTool(Name = "qa_audio_abort"), Description("Aborts process-loopback capture and destroys its incomplete temporary artifact.")]
    public static async Task<string> AudioAbort(IWorkerControlService worker, CancellationToken cancellationToken) =>
        Json(await worker.AudioAbortAsync(cancellationToken));

    [McpServerTool(Name = "qa_rolling_start"), Description("Starts a bounded rolling HWND-only evidence capture under the current launcher proof and PID lease. Bundle names are confined to the artifact root; optional MP4 uses only trusted configured FFmpeg path/hash.")]
    public static async Task<string> RollingStart(IWorkerControlService worker, string bundleName,
        int framesPerSecond, int ringSeconds, bool encodeMp4, CancellationToken cancellationToken) =>
        Json(await worker.RollingStartAsync(bundleName, framesPerSecond, ringSeconds, encodeMp4, cancellationToken));

    [McpServerTool(Name = "qa_rolling_state"), Description("Returns the active or finalized rolling capture state without changing capture.")]
    public static async Task<string> RollingState(IWorkerControlService worker, CancellationToken cancellationToken) =>
        Json(await worker.RollingStateAsync(cancellationToken));

    [McpServerTool(Name = "qa_rolling_stop"), Description("Stops the matching rolling capture and atomically finalizes its hash-chained evidence bundle.")]
    public static async Task<string> RollingStop(IWorkerControlService worker, string captureId,
        CancellationToken cancellationToken) => Json(await worker.RollingStopAsync(captureId, cancellationToken));

    [McpServerTool(Name = "qa_rolling_abort"), Description("Aborts the active rolling capture; proof/lease loss and worker teardown also invoke this automatically.")]
    public static async Task<string> RollingAbort(IWorkerControlService worker, CancellationToken cancellationToken) =>
        Json(await worker.RollingAbortAsync(cancellationToken));

    [McpServerTool(Name = "qa_navigate_observed"), Description("Executes one bounded navigation step using only authenticated sole-player position and server-authored objective, fallback, or gate coordinates.")]
    public static async Task<string> NavigateObserved(
        AuthenticatedObserverControls controls,
        [Description("One of: objective, fallback, gate.")] string targetKind,
        [Description("Server-authored target id; optional only for the unique current objective/fallback.")] string? targetId,
        [Description("Arrival radius in blocks, clamped to 0.5 through 8; gate approaches are additionally capped at 1.0 to preserve the interaction centerline.")] double within,
        CancellationToken cancellationToken) =>
        Json(await controls.NavigateAsync(targetKind, targetId, within, cancellationToken));

    [McpServerTool(Name = "qa_interact_observed"), Description("Aims and performs Hytale's secondary block-use interaction on a server-authored objective or gate target from an authenticated sole-player observation. No coordinates are accepted from the caller.")]
    public static async Task<string> InteractObserved(
        AuthenticatedObserverControls controls,
        [Description("One of: objective, fallback, gate.")] string targetKind,
        [Description("Server-authored target id, if selecting a gate.")] string? targetId,
        CancellationToken cancellationToken) =>
        Json(await controls.InteractAsync(targetKind, targetId, cancellationToken));

    [McpServerTool(Name = "qa_combat_observed_current_run"), Description("Executes one bounded combat-reflex step against only authenticated current-run hostile encounters. Caller-supplied entities, positions, and stats are not accepted.")]
    public static async Task<string> CombatObserved(
        AuthenticatedObserverControls controls, CancellationToken cancellationToken) =>
        Json(await controls.CombatAsync(cancellationToken));

    [McpServerTool(Name = "qa_input_release_all"), Description("Releases every key and mouse button tracked by the worker.")]
    public static async Task<string> ReleaseAll(IWorkerControlService worker, CancellationToken cancellationToken)
    {
        await worker.ReleaseAllAsync(cancellationToken);
        return Json(new { released = true });
    }

    [McpServerTool(Name = "qa_scenarios_list"), Description("Lists deterministic offline Hytale QA scenarios and their evidence modes.")]
    public static string ListScenarios(ScenarioCatalog scenarios) => Json(scenarios.List());

    [McpServerTool(Name = "qa_scenarios_validate"), Description("Validates scenario safety, offline, evidence-mode, and one-client contracts.")]
    public static string ValidateScenarios(ScenarioCatalog scenarios) => Json(scenarios.Validate());

    [McpServerTool(Name = "qa_suites_list"), Description("Lists serial offline QA suite manifests and their complete scenario order.")]
    public static string ListSuites(SuiteCatalog suites) => Json(suites.List());

    [McpServerTool(Name = "qa_suites_validate"), Description("Validates suite references, offline proof, one-client serialization, evidence criteria, and release-gate presence.")]
    public static string ValidateSuites(SuiteCatalog suites) => Json(suites.Validate());

    [McpServerTool(Name = "qa_capabilities_audit"), Description("Reports every operation used by the configured scenario catalog, its physical/read-only/capture/unsupported automation level, observer contract, and fully adapter-backed scenario count.")]
    public static string AuditCapabilities(QaCapabilityAuditor capabilities) => Json(capabilities.Audit());

    [McpServerTool(Name = "qa_scenario_run"), Description("Starts one durable launcher-owned offline scenario run behind the exclusive serial run lease. Before physical attachment, runtime preflight blocks unsupported capabilities and any player/seed/snapshot/loadout fixture not proven by the authenticated observer. Returns immediately; use qa_run_state/result to monitor.")]
    public static async Task<string> RunScenario(
        QaRunCoordinator runs,
        [Description("Scenario id from qa_scenarios_list.")] string scenarioId,
        [Description("Existing Hytale client PID pinned by launcher evidence.")] int clientProcessId,
        [Description("Plain launcher proof JSON file name.")] string launcherEvidenceFileName,
        CancellationToken cancellationToken) =>
        Json(await runs.StartScenarioAsync(scenarioId, clientProcessId, launcherEvidenceFileName, cancellationToken));

    [McpServerTool(Name = "qa_suite_run"), Description("Starts a durable serial suite with the first existing launcher-owned OFFLINE proof. A fresh-session suite pauses between scenarios; it never automates launcher or authentication.")]
    public static async Task<string> RunSuite(
        QaRunCoordinator runs,
        [Description("Suite id from qa_suites_list.")] string suiteId,
        [Description("Existing Hytale client PID pinned by launcher evidence.")] int clientProcessId,
        [Description("Plain launcher proof JSON file name.")] string launcherEvidenceFileName,
        CancellationToken cancellationToken) =>
        Json(await runs.StartSuiteAsync(suiteId, clientProcessId, launcherEvidenceFileName, cancellationToken));

    [McpServerTool(Name = "qa_suite_handoff"), Description("Continues a suite that is awaiting its next scenario only after independently validating a newly user-provisioned launcher-owned OFFLINE proof and new client PID. It does not launch Hytale, select auth, or close the previous session.")]
    public static async Task<string> HandoffSuite(
        QaRunCoordinator runs,
        [Description("Run UUID currently in awaitingHandoff phase.")] string runId,
        [Description("New Hytale client PID pinned by the new launcher proof.")] int clientProcessId,
        [Description("New plain launcher proof JSON file name.")] string launcherEvidenceFileName,
        CancellationToken cancellationToken) =>
        Json(await runs.HandoffSuiteAsync(runId, clientProcessId, launcherEvidenceFileName,
            cancellationToken));

    [McpServerTool(Name = "qa_run_handoff"), Description("Continues a scenario paused at client.reconnect or server.restart only after validating a renewed launcher-owned OFFLINE proof for the same one-client PID and world. It never clicks launcher UI, restarts a process, authenticates, or accepts caller coordinates.")]
    public static async Task<string> HandoffRun(
        QaRunCoordinator runs,
        [Description("Run UUID currently in awaitingHandoff phase.")] string runId,
        [Description("The unchanged Hytale client PID pinned by the renewed proof.")] int clientProcessId,
        [Description("New plain launcher proof JSON file name.")] string launcherEvidenceFileName,
        CancellationToken cancellationToken) =>
        Json(await runs.HandoffRunAsync(runId, clientProcessId, launcherEvidenceFileName,
            cancellationToken));

    [McpServerTool(Name = "qa_run_state"), Description("Returns the current durable serial-run cursor and phase without changing the run.")]
    public static string RunState(QaRunCoordinator runs) => Json(runs.State);

    [McpServerTool(Name = "qa_run_cancel"), Description("Requests cancellation of the matching active run; physical input release and worker teardown still execute.")]
    public static async Task<string> CancelRun(QaRunCoordinator runs, string runId, CancellationToken cancellationToken) =>
        Json(await runs.CancelAsync(runId, cancellationToken));

    [McpServerTool(Name = "qa_run_result"), Description("Reads a completed durable run result and evidence-root locations by run UUID.")]
    public static string RunResult(QaRunCoordinator runs, string runId) => Json(runs.Result(runId));

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);
}
