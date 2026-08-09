using System.Security.Cryptography;
using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Orchestrator;

public enum WorkerControlPhase { Detached, Attaching, Armed, Faulted, Detaching }

public sealed record WorkerControlState(
    WorkerControlPhase Phase,
    int? ClientProcessId,
    string? LeaseId,
    OfflineProofBoundaryKind? Boundary,
    DateTimeOffset? LastHeartbeatUtc,
    string? FaultCode,
    string Message);

public sealed record SemanticControlResult(
    string Kind,
    IReadOnlyList<PhysicalIntent> Intents,
    SafetyCheck Safety,
    DateTimeOffset CompletedAtUtc);

public interface IWorkerControlService : IAsyncDisposable
{
    WorkerControlState State { get; }
    Task<WorkerControlState> AttachAsync(int processId, string sessionId, AssistanceMode assistanceMode,
        EvidenceCapabilities capabilities, CancellationToken cancellationToken);
    Task<WorkerControlState> AttachLauncherAsync(int processId, string evidenceFileName, AssistanceMode assistanceMode,
        EvidenceCapabilities capabilities, CancellationToken cancellationToken);
    Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken);
    Task<SemanticControlResult> InteractAsync(string semanticInput, CancellationToken cancellationToken);
    Task<SemanticControlResult> UseAbilityAsync(string semanticInput, CancellationToken cancellationToken);
    Task<WorkerControlState> DetachAsync(CancellationToken cancellationToken);
    Task StopDueToProofLossAsync(string reason, CancellationToken cancellationToken);
    Task<SafetyCheck> SafetyAsync(bool requireForeground, CancellationToken cancellationToken);
    Task<bool> FocusAsync(CancellationToken cancellationToken);
    Task<CaptureArtifact> ScreenshotAsync(string fileName, CancellationToken cancellationToken);
    Task<AudioCaptureState> AudioStartAsync(string fileName, CancellationToken cancellationToken);
    Task<AudioCaptureState> AudioStateAsync(CancellationToken cancellationToken);
    Task<AudioCaptureArtifact> AudioStopAsync(string captureId, CancellationToken cancellationToken);
    Task<AudioCaptureState> AudioAbortAsync(CancellationToken cancellationToken);
    Task<JsonElement> RollingStartAsync(string bundleName, int framesPerSecond, int ringSeconds,
        bool encodeMp4, CancellationToken cancellationToken);
    Task<JsonElement> RollingStateAsync(CancellationToken cancellationToken);
    Task<JsonElement> RollingStopAsync(string captureId, CancellationToken cancellationToken);
    Task<JsonElement> RollingAbortAsync(CancellationToken cancellationToken);
    Task<SemanticControlResult> NavigateStepAsync(NavigationState state, CancellationToken cancellationToken);
    Task<SemanticControlResult> AimStepAsync(AimState state, CancellationToken cancellationToken);
    Task<SemanticControlResult> CombatStepAsync(CombatState state, CancellationToken cancellationToken);
    Task<SemanticControlResult> ClickUiAsync(UiSemanticNode node, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Semantic UI input is not implemented by this worker service.");
    Task ReleaseAllAsync(CancellationToken cancellationToken);
}

public sealed class WorkerControlService : IWorkerControlService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HeartbeatCycleTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CleanupRpcTimeout = TimeSpan.FromMilliseconds(250);
    private readonly QaPaths paths;
    private readonly IWorkerRpcClientFactory clients;
    private readonly IWorkerOfflineProofProviderFactory proofs;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim actionGate = new(1, 1);
    private IWorkerRpcClient? client;
    private IWorkerOfflineProofProvider? proofProvider;
    private ClientLease? lease;
    private OfflineServerProof? pinnedProof;
    private string? rollingCaptureId;
    private string? activeArtifactDirectory;
    private CancellationTokenSource? heartbeatCancellation;
    private Task? heartbeatTask;
    private WorkerControlState state = new(WorkerControlPhase.Detached, null, null, null, null, null,
        "No worker is attached.");

    public WorkerControlService(QaPaths paths, IWorkerRpcClientFactory clients,
        IWorkerOfflineProofProviderFactory proofs, TimeProvider? time = null)
    {
        this.paths = paths;
        this.clients = clients;
        this.proofs = proofs;
        this.time = time ?? TimeProvider.System;
    }

    public WorkerControlState State => state;

    public async Task<WorkerControlState> AttachAsync(int processId, string sessionId,
        AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        throw new NotSupportedException(
            "docker-physical-attach-disabled: dedicated OFFLINE proof cannot correlate an unrelated retail client; use launcher-owned singleplayer proof");
    }

    public async Task<WorkerControlState> AttachLauncherAsync(int processId, string evidenceFileName,
        AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken) =>
        await AttachProviderAsync(processId, proofs.CreateLauncher(evidenceFileName), assistanceMode, capabilities,
            null,
            cancellationToken).ConfigureAwait(false);

    private async Task<WorkerControlState> AttachProviderAsync(int processId, IWorkerOfflineProofProvider provider,
        AssistanceMode assistanceMode, EvidenceCapabilities capabilities, string? sessionIdOverride,
        CancellationToken cancellationToken)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        var sessionId = sessionIdOverride ?? provider.SessionId
            ?? throw new InvalidDataException("Offline proof provider did not supply a session id.");
        if (!Guid.TryParse(sessionId, out var parsed) || parsed == Guid.Empty)
            throw new InvalidDataException("Offline proof provider session id must be a non-empty UUID.");
        if (!capabilities.PhysicalInput || capabilities.Teleport || capabilities.DirectDamage || capabilities.FaultInjection)
            throw new InvalidOperationException("Worker attachment permits physical input/observer/combat-reflex only.");
        if (assistanceMode == AssistanceMode.WhiteBox)
            throw new InvalidOperationException("The MCP physical-control surface does not arm white-box mutation mode.");

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (state.Phase is not WorkerControlPhase.Detached and not WorkerControlPhase.Faulted)
                throw new InvalidOperationException("A QA worker is already attached or attaching.");
            state = new(WorkerControlPhase.Attaching, processId, null, null, null, null,
                "Verifying offline proof before starting the worker.");
            IWorkerRpcClient? started = null;
            try
            {
                var proof = await provider.GetFreshProofAsync(cancellationToken).ConfigureAwait(false);
                ValidateProof(proof, provider.Boundary, sessionId, processId, paths);
                var initialProofAge = time.GetUtcNow() - proof.ObservedAtUtc;
                if (initialProofAge < TimeSpan.FromSeconds(-1) || initialProofAge > TimeSpan.FromMilliseconds(1500))
                    throw new InvalidOperationException("Initial offline observer proof timestamp is stale or future-dated.");
                var clientIdentity = ReadAndVerifyClientIdentity();
                activeArtifactDirectory = paths.SessionArtifactDirectory(sessionId);
                Directory.CreateDirectory(activeArtifactDirectory);
                paths.RequireSafeEvidencePath(activeArtifactDirectory);
                var tracePath = Path.Combine(activeArtifactDirectory, $"worker-{processId}-{Guid.NewGuid():N}.ndjson");
                started = await clients.StartAsync(tracePath, activeArtifactDirectory, cancellationToken).ConfigureAwait(false);
                var workerCapabilities = await started.CallAsync<WorkerCapabilities>(
                    "worker.capabilities", null, cancellationToken).ConfigureAwait(false);
                if (workerCapabilities.CanLaunchClient || !workerCapabilities.CanAttachClient ||
                    !workerCapabilities.SupportsRelativeMouse || !workerCapabilities.SupportsClientPointer ||
                    !workerCapabilities.SupportsHeldKeys)
                    throw new InvalidOperationException("Worker capabilities do not satisfy the physical-control contract.");

                var policy = new SessionSafetyPolicy(sessionId, proof.ObserverNonce, assistanceMode,
                    OfflineAttested: true, proof.ServerEndpoint, "HytaleClient.exe",
                    [clientIdentity.Path], [clientIdentity.Sha256], RequireSingleMatchingProcess: true);
                var acquired = await started.CallAsync<ClientLease>("lease.acquire", new
                {
                    processId,
                    policy,
                    correlationId = Correlation("attach")
                }, cancellationToken).ConfigureAwait(false);
                var armed = await started.CallAsync<SessionState>("session.arm", new
                {
                    leaseId = acquired.LeaseId,
                    correlationId = Correlation("arm"),
                    offlineProof = proof,
                    capabilities
                }, cancellationToken).ConfigureAwait(false);
                if (!armed.Armed || armed.LeaseId != acquired.LeaseId)
                    throw new InvalidOperationException("Worker did not enter the armed state.");

                client = started;
                proofProvider = provider;
                lease = acquired;
                pinnedProof = proof;
                heartbeatCancellation = new CancellationTokenSource();
                state = new(WorkerControlPhase.Armed, processId, acquired.LeaseId, provider.Boundary,
                    time.GetUtcNow(), null, "Worker is armed by a fresh offline proof.");
                heartbeatTask = Task.Run(() => HeartbeatLoopAsync(heartbeatCancellation.Token));
                return state;
            }
            catch (Exception failure)
            {
                if (started is not null)
                {
                    try { await started.TerminateAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    await started.DisposeAsync().ConfigureAwait(false);
                }
                provider.Dispose();
                ResetRuntime();
                state = new(WorkerControlPhase.Faulted, processId, null, null, null,
                    "worker-attach-failed", failure.Message);
                throw;
            }
        }
        finally { lifecycleGate.Release(); }
    }

    public async Task<WorkerControlState> DetachAsync(CancellationToken cancellationToken)
    {
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (client is null)
            {
                ResetRuntime();
                state = new(WorkerControlPhase.Detached, null, null, null, null, null, "No worker is attached.");
                return state;
            }
            state = state with { Phase = WorkerControlPhase.Detaching, Message = "Releasing input and terminating the worker." };
            var currentClient = client;
            var currentLease = lease;
            heartbeatCancellation?.Cancel();
            if (heartbeatTask is not null)
            {
                try { await heartbeatTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            if (currentLease is not null)
            {
                if (rollingCaptureId is not null)
                    await BestEffortCallAsync(currentClient, "rolling.abort", new
                    { leaseId = currentLease.LeaseId, correlationId = Correlation("detach-rolling"), captureId = rollingCaptureId }).ConfigureAwait(false);
                await BestEffortCallAsync(currentClient, "input.releaseAll", new
                { leaseId = currentLease.LeaseId, correlationId = Correlation("detach-release") }).ConfigureAwait(false);
                await BestEffortCallAsync(currentClient, "audio.abort", new
                { leaseId = currentLease.LeaseId, correlationId = Correlation("detach-audio") }).ConfigureAwait(false);
                await BestEffortCallAsync(currentClient, "lease.release", new
                { leaseId = currentLease.LeaseId, correlationId = Correlation("detach-lease") }).ConfigureAwait(false);
            }
            await currentClient.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            await currentClient.DisposeAsync().ConfigureAwait(false);
            ResetRuntime();
            state = new(WorkerControlPhase.Detached, null, null, null, null, null,
                "Worker terminated and all tracked input was released.");
            return state;
        }
        finally { lifecycleGate.Release(); }
    }

    public Task StopDueToProofLossAsync(string reason, CancellationToken cancellationToken) =>
        EmergencyStopAsync("offline-proof-lost", reason, cancellationToken);

    public async Task<SafetyCheck> SafetyAsync(bool requireForeground, CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        return await current.CallAsync<SafetyCheck>("safety.check", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("safety"),
            requireForeground
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> FocusAsync(CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        var result = await current.CallAsync<JsonElement>("lease.focus", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("focus")
        }, cancellationToken).ConfigureAwait(false);
        return result.TryGetProperty("focused", out var focused) && focused.GetBoolean();
    }

    public async Task<CaptureArtifact> ScreenshotAsync(string fileName, CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        var destination = ArtifactPath(fileName, ".bmp");
        return await current.CallAsync<CaptureArtifact>("capture.screenshot", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("screenshot"),
            destinationPath = destination
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureState> AudioStartAsync(string fileName, CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        var destination = ArtifactPath(fileName, ".wav");
        return await current.CallAsync<AudioCaptureState>("audio.start", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("audio-start"),
            destinationPath = destination
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureState> AudioStateAsync(CancellationToken cancellationToken)
    {
        var (current, _) = RequireArmed();
        return await current.CallAsync<AudioCaptureState>("audio.state", null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureArtifact> AudioStopAsync(string captureId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captureId)) throw new ArgumentException("Capture id is required.", nameof(captureId));
        var (current, currentLease) = RequireArmed();
        return await current.CallAsync<AudioCaptureArtifact>("audio.stop", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("audio-stop"),
            captureId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AudioCaptureState> AudioAbortAsync(CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        return await current.CallAsync<AudioCaptureState>("audio.abort", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("audio-abort")
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RollingStartAsync(string bundleName, int framesPerSecond, int ringSeconds,
        bool encodeMp4, CancellationToken cancellationToken)
    {
        if (framesPerSecond is < 1 or > 10)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "Frame rate must be between 1 and 10 FPS.");
        if (ringSeconds is < 1 or > 300)
            throw new ArgumentOutOfRangeException(nameof(ringSeconds), "Ring duration must be between 1 and 300 seconds.");
        var (current, currentLease) = RequireArmed();
        if (rollingCaptureId is not null) throw new InvalidOperationException("A rolling capture is already active.");
        var bundle = ArtifactBundlePath(bundleName);
        var mp4 = encodeMp4 ? Path.Combine(ActiveArtifactDirectory(), bundleName + ".mp4") : null;
        var result = await current.CallAsync<JsonElement>("rolling.start", new
        {
            leaseId = currentLease.LeaseId, correlationId = Correlation("rolling-start"),
            bundleDirectory = bundle, framesPerSecond, ringSeconds, finalMp4Path = mp4
        }, cancellationToken).ConfigureAwait(false);
        rollingCaptureId = result.GetProperty("captureId").GetString()
            ?? throw new InvalidDataException("Worker rolling capture returned no capture id.");
        return result;
    }

    public async Task<JsonElement> RollingStateAsync(CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        return await current.CallAsync<JsonElement>("rolling.state", new
        { leaseId = currentLease.LeaseId, correlationId = Correlation("rolling-state") }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> RollingStopAsync(string captureId, CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        if (rollingCaptureId is null || !FixedEquals(rollingCaptureId, captureId))
            throw new InvalidOperationException("Rolling capture id does not match the active capture.");
        try
        {
            return await current.CallAsync<JsonElement>("rolling.stop", new
            { leaseId = currentLease.LeaseId, correlationId = Correlation("rolling-stop"), captureId }, cancellationToken).ConfigureAwait(false);
        }
        finally { rollingCaptureId = null; }
    }

    public async Task<JsonElement> RollingAbortAsync(CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        if (rollingCaptureId is null) return JsonSerializer.SerializeToElement(new { status = "idle" });
        var captureId = rollingCaptureId;
        try
        {
            return await current.CallAsync<JsonElement>("rolling.abort", new
            { leaseId = currentLease.LeaseId, correlationId = Correlation("rolling-abort"), captureId }, cancellationToken).ConfigureAwait(false);
        }
        finally { rollingCaptureId = null; }
    }

    public Task<SemanticControlResult> NavigateStepAsync(NavigationState state, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("navigate", DeterministicNavigator.Decide(ValidateNavigation(state)), cancellationToken);

    public Task<SemanticControlResult> AimStepAsync(AimState state, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("aim", [DeterministicAim.Decide(ValidateAim(state))], cancellationToken);

    public Task<SemanticControlResult> CombatStepAsync(CombatState state, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("combat", OfflineCombatReflex.Decide(ValidateCombat(state)), cancellationToken);

    public Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken)
    {
        _ = RequireArmed();
        return (proofProvider ?? throw new InvalidOperationException("Offline proof provider disappeared."))
            .ObserveSolePlayerAsync(cancellationToken);
    }

    public Task<SemanticControlResult> InteractAsync(string semanticInput, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("interact", [InputIntent(semanticInput, ability: false)], cancellationToken);

    public Task<SemanticControlResult> UseAbilityAsync(string semanticInput, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("ability", [InputIntent(semanticInput, ability: true)], cancellationToken);

    public Task<SemanticControlResult> ClickUiAsync(UiSemanticNode node, CancellationToken cancellationToken) =>
        ExecuteIntentsAsync("semantic-ui", UiSemanticResolver.Click(node), cancellationToken);

    public async Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        await current.CallAsync<JsonElement>("input.releaseAll", new
        {
            leaseId = currentLease.LeaseId,
            correlationId = Correlation("release-all")
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SemanticControlResult> ExecuteIntentsAsync(string kind,
        IReadOnlyList<PhysicalIntent> intents, CancellationToken cancellationToken)
    {
        ValidateIntents(intents);
        await actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var safety = await SafetyAsync(requireForeground: true, cancellationToken).ConfigureAwait(false);
            if (!safety.Safe) throw new InvalidOperationException($"{safety.Code}: {safety.Message}");
            foreach (var intent in intents)
                await ExecuteIntentAsync(intent, cancellationToken).ConfigureAwait(false);
            safety = await SafetyAsync(requireForeground: true, cancellationToken).ConfigureAwait(false);
            if (!safety.Safe) throw new InvalidOperationException($"{safety.Code}: {safety.Message}");
            return new(kind, intents, safety, time.GetUtcNow());
        }
        catch (Exception failure)
        {
            try { await ReleaseAllAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await EmergencyStopAsync("semantic-control-failed", failure.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally { actionGate.Release(); }
    }

    private async Task ExecuteIntentAsync(PhysicalIntent intent, CancellationToken cancellationToken)
    {
        var (current, currentLease) = RequireArmed();
        switch (intent.Kind)
        {
            case PhysicalIntentKind.ReleaseAll:
                await ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.MoveForward:
                await TimedKeyAsync(current, currentLease, 0x57, intent.DurationMilliseconds, 500, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.MoveBackward:
                await TimedKeyAsync(current, currentLease, 0x53, intent.DurationMilliseconds, 500, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.StrafeLeft:
                await TimedKeyAsync(current, currentLease, 0x41, intent.DurationMilliseconds, 500, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.StrafeRight:
                await TimedKeyAsync(current, currentLease, 0x44, intent.DurationMilliseconds, 500, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.Jump:
                await TimedKeyAsync(current, currentLease, 0x20, intent.DurationMilliseconds, 120, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.Turn:
                await MouseMoveAsync(current, currentLease, checked((int)Math.Round(Math.Clamp(intent.X * 12, -240, 240))), 0,
                    cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.Look:
                await MouseMoveAsync(current, currentLease, checked((int)Math.Round(Math.Clamp(intent.X, -240, 240))),
                    checked((int)Math.Round(Math.Clamp(intent.Y, -180, 180))), cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.PointerMoveClient:
                await current.CallAsync<JsonElement>("input.pointerMoveClient", new
                {
                    leaseId = currentLease.LeaseId,
                    correlationId = Correlation("semantic-ui-pointer"),
                    normalizedX = intent.X,
                    normalizedY = intent.Y
                }, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.PrimaryClick:
                await TimedMouseAsync(current, currentLease, MouseButton.Left, intent.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.SecondaryClick:
                await TimedMouseAsync(current, currentLease, MouseButton.Right, intent.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.PressKey when string.Equals(intent.Key, "X1", StringComparison.Ordinal):
                await TimedMouseAsync(current, currentLease, MouseButton.X1, intent.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.PressKey when string.Equals(intent.Key, "X2", StringComparison.Ordinal):
                await TimedMouseAsync(current, currentLease, MouseButton.X2, intent.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            case PhysicalIntentKind.PressKey when string.Equals(intent.Key, "MIDDLE", StringComparison.Ordinal):
                await TimedMouseAsync(current, currentLease, MouseButton.Middle, intent.DurationMilliseconds, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Physical intent is not allowlisted: {intent.Kind}/{intent.Key}");
        }
    }

    private async Task TimedKeyAsync(IWorkerRpcClient current, ClientLease currentLease, ushort virtualKey,
        int requestedDuration, int maximumDuration, CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(requestedDuration, 20, maximumDuration);
        await KeyAsync(current, currentLease, virtualKey, KeyTransition.Down, cancellationToken).ConfigureAwait(false);
        try { await Task.Delay(TimeSpan.FromMilliseconds(duration), time, cancellationToken).ConfigureAwait(false); }
        finally { await KeyAsync(current, currentLease, virtualKey, KeyTransition.Up, CancellationToken.None).ConfigureAwait(false); }
    }

    private async Task TimedMouseAsync(IWorkerRpcClient current, ClientLease currentLease, MouseButton button,
        int requestedDuration, CancellationToken cancellationToken)
    {
        var duration = Math.Clamp(requestedDuration, 20, 100);
        await MouseButtonAsync(current, currentLease, button, KeyTransition.Down, cancellationToken).ConfigureAwait(false);
        try { await Task.Delay(TimeSpan.FromMilliseconds(duration), time, cancellationToken).ConfigureAwait(false); }
        finally { await MouseButtonAsync(current, currentLease, button, KeyTransition.Up, CancellationToken.None).ConfigureAwait(false); }
    }

    private Task<JsonElement> KeyAsync(IWorkerRpcClient current, ClientLease currentLease, ushort virtualKey,
        KeyTransition transition, CancellationToken cancellationToken) =>
        current.CallAsync<JsonElement>("input.key", new
        {
            leaseId = currentLease.LeaseId, correlationId = Correlation("semantic-key"), virtualKey, transition
        }, cancellationToken);

    private Task<JsonElement> MouseButtonAsync(IWorkerRpcClient current, ClientLease currentLease, MouseButton button,
        KeyTransition transition, CancellationToken cancellationToken) =>
        current.CallAsync<JsonElement>("input.mouseButton", new
        {
            leaseId = currentLease.LeaseId, correlationId = Correlation("semantic-button"), button, transition
        }, cancellationToken);

    private Task<JsonElement> MouseMoveAsync(IWorkerRpcClient current, ClientLease currentLease, int deltaX, int deltaY,
        CancellationToken cancellationToken) => current.CallAsync<JsonElement>("input.mouseMoveRelative", new
        {
            leaseId = currentLease.LeaseId, correlationId = Correlation("semantic-look"), deltaX, deltaY
        }, cancellationToken);

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(HeartbeatInterval, time, cancellationToken).ConfigureAwait(false);
                var current = client ?? throw new InvalidOperationException("Worker client disappeared.");
                var currentLease = lease ?? throw new InvalidOperationException("Worker lease disappeared.");
                var provider = proofProvider ?? throw new InvalidOperationException("Offline proof provider disappeared.");
                using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cycle.CancelAfter(HeartbeatCycleTimeout);
                var proof = await provider.GetFreshProofAsync(cycle.Token).ConfigureAwait(false);
                if (!SameProofBoundary(pinnedProof!, proof))
                    throw new InvalidOperationException("Offline proof identity drifted.");
                var proofAge = time.GetUtcNow() - proof.ObservedAtUtc;
                if (proofAge < TimeSpan.FromSeconds(-1) || proofAge > TimeSpan.FromMilliseconds(1500))
                    throw new InvalidOperationException("Offline observer proof timestamp is stale or future-dated.");
                await current.CallAsync<SessionState>("session.heartbeat", new
                {
                    leaseId = currentLease.LeaseId,
                    correlationId = Correlation("heartbeat"),
                    offlineProof = proof
                }, cycle.Token).ConfigureAwait(false);
                pinnedProof = proof;
                state = state with { LastHeartbeatUtc = time.GetUtcNow(), Message = "Offline proof heartbeat is current." };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception failure)
        {
            await EmergencyStopAsync("offline-proof-lost", failure.Message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task EmergencyStopAsync(string code, string reason, CancellationToken cancellationToken)
    {
        var current = Interlocked.Exchange(ref client, null);
        var currentLease = lease;
        heartbeatCancellation?.Cancel();
        state = new(WorkerControlPhase.Faulted, state.ClientProcessId, currentLease?.LeaseId,
            proofProvider?.Boundary, state.LastHeartbeatUtc, code,
            $"Worker was terminated fail-closed: {reason}");
        if (current is null)
        {
            ResetRuntime();
            return;
        }
        if (currentLease is not null)
        {
            if (rollingCaptureId is not null)
                await BestEffortCallAsync(current, "rolling.abort", new
                { leaseId = currentLease.LeaseId, correlationId = Correlation("emergency-rolling"), captureId = rollingCaptureId }).ConfigureAwait(false);
            await BestEffortCallAsync(current, "input.releaseAll", new
            { leaseId = currentLease.LeaseId, correlationId = Correlation("emergency-release") }).ConfigureAwait(false);
            await BestEffortCallAsync(current, "audio.abort", new
            { leaseId = currentLease.LeaseId, correlationId = Correlation("emergency-audio") }).ConfigureAwait(false);
        }
        try { await current.TerminateAsync(cancellationToken).ConfigureAwait(false); }
        catch { await current.TerminateAsync(CancellationToken.None).ConfigureAwait(false); }
        await current.DisposeAsync().ConfigureAwait(false);
        ResetRuntime();
    }

    private static async Task BestEffortCallAsync(IWorkerRpcClient current, string method, object parameters)
    {
        using var timeout = new CancellationTokenSource(CleanupRpcTimeout);
        try { await current.CallAsync<JsonElement>(method, parameters, timeout.Token).ConfigureAwait(false); }
        catch { }
    }

    private (IWorkerRpcClient Client, ClientLease Lease) RequireArmed()
    {
        var current = client;
        var currentLease = lease;
        if (state.Phase != WorkerControlPhase.Armed || current is null || currentLease is null || !current.IsAlive)
            throw new InvalidOperationException("QA worker is not armed.");
        return (current, currentLease);
    }

    private (string Path, string Sha256) ReadAndVerifyClientIdentity()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(paths.ClientCapabilityPath));
        var expected = document.RootElement.GetProperty("clientSha256").GetString()
            ?? throw new InvalidDataException("Client capability record has no SHA-256.");
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("Client capability SHA-256 is invalid.");
        var path = Path.GetFullPath(paths.ClientExecutablePath);
        using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(input));
        if (!FixedEquals(expected, actual))
            throw new InvalidOperationException("Installed Hytale client SHA-256 differs from the audited allowlist.");
        return (path, actual);
    }

    private string ArtifactPath(string fileName, string requiredExtension)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
            !string.Equals(Path.GetExtension(fileName), requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Artifact must be a plain {requiredExtension} file name.");
        var root = ActiveArtifactDirectory();
        paths.RequireSafeEvidenceTree(root);
        Directory.CreateDirectory(root);
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Artifact path escaped the isolated artifact directory.");
        return path;
    }

    private string ArtifactBundlePath(string bundleName)
    {
        if (string.IsNullOrWhiteSpace(bundleName) || bundleName != Path.GetFileName(bundleName) ||
            bundleName.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_'))
            throw new InvalidOperationException("Rolling bundle name must contain only letters, digits, dash, or underscore.");
        var root = ActiveArtifactDirectory();
        paths.RequireSafeEvidenceTree(root);
        Directory.CreateDirectory(root);
        return Path.GetFullPath(Path.Combine(root, bundleName));
    }

    private string ActiveArtifactDirectory() => activeArtifactDirectory
        ?? throw new InvalidOperationException("No session-scoped artifact directory is active.");

    private static void ValidateIntents(IReadOnlyList<PhysicalIntent> intents)
    {
        if (intents.Count is < 1 or > 8) throw new InvalidOperationException("Semantic step emitted an invalid intent count.");
        var duration = intents.Sum(intent => Math.Max(0, intent.DurationMilliseconds));
        if (duration > 1500) throw new InvalidOperationException("Semantic step exceeded the 1500 ms physical-input budget.");
        if (intents.Any(intent => !Enum.IsDefined(intent.Kind)))
            throw new InvalidOperationException("Semantic step emitted an unknown physical intent.");
    }

    private static PhysicalIntent InputIntent(string semanticInput, bool ability) => semanticInput switch
    {
        "left_click" => new(PhysicalIntentKind.PrimaryClick, DurationMilliseconds: 40),
        "right_click" => new(PhysicalIntentKind.SecondaryClick, DurationMilliseconds: 40),
        "middle_click" when ability => new(PhysicalIntentKind.PressKey, Key: "MIDDLE", DurationMilliseconds: 40),
        "x1" when ability => new(PhysicalIntentKind.PressKey, Key: "X1", DurationMilliseconds: 40),
        "x2" when ability => new(PhysicalIntentKind.PressKey, Key: "X2", DurationMilliseconds: 40),
        _ => throw new InvalidOperationException("Semantic input is not allowlisted for this action.")
    };

    private static void ValidateProof(OfflineServerProof proof, OfflineProofBoundaryKind boundary,
        string sessionId, int processId, QaPaths paths)
    {
        var common = proof.Offline && string.Equals(proof.AuthMode, "offline", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(proof.ObserverNonce) is false && proof.ServerSha256.Length == 64;
        var exactBoundary = boundary switch
        {
            OfflineProofBoundaryKind.IsolatedDockerDedicated =>
                proof.Kind == OfflineProofKind.DockerDedicated && proof.Launcher is null &&
                string.Equals(proof.ServerEndpoint, paths.DockerEndpoint, StringComparison.Ordinal) &&
                string.Equals(proof.DockerProject, paths.DockerProject, StringComparison.Ordinal) &&
                string.Equals(proof.DockerContainer, paths.DockerContainer, StringComparison.Ordinal) &&
                string.Equals(proof.DockerNetwork, paths.DockerNetwork, StringComparison.Ordinal),
            OfflineProofBoundaryKind.LauncherOwnedSingleplayer =>
                proof.Kind == OfflineProofKind.LauncherOwnedSingleplayer && proof.Launcher is not null &&
                proof.Launcher.Client.ProcessId == processId && string.IsNullOrEmpty(proof.DockerProject) &&
                string.IsNullOrEmpty(proof.DockerContainer) && string.IsNullOrEmpty(proof.DockerNetwork),
            _ => false
        };
        if (!common || !exactBoundary)
            throw new InvalidOperationException($"Offline proof does not satisfy the worker boundary for session {sessionId}.");
    }

    private static NavigationState ValidateNavigation(NavigationState state)
    {
        RequireFinite(state.Position, nameof(state.Position));
        RequireFinite(state.Target, nameof(state.Target));
        if (!double.IsFinite(state.YawDegrees) || state.NoProgress < TimeSpan.Zero)
            throw new InvalidOperationException("Navigation state contains non-finite or negative values.");
        return state;
    }

    private static AimState ValidateAim(AimState state)
    {
        RequireFinite(state.Eye, nameof(state.Eye));
        RequireFinite(state.Target, nameof(state.Target));
        if (!double.IsFinite(state.YawDegrees) || !double.IsFinite(state.PitchDegrees) ||
            state.PitchDegrees is < -90 or > 90)
            throw new InvalidOperationException("Aim state contains invalid angles.");
        return state;
    }

    private static CombatState ValidateCombat(CombatState state)
    {
        RequireFinite(state.Eye, nameof(state.Eye));
        if (!double.IsFinite(state.YawDegrees) || !double.IsFinite(state.PitchDegrees) ||
            state.PitchDegrees is < -90 or > 90 || state.SignatureEnergy is < 0 or > 100 ||
            state.Candidates.Count > 128)
            throw new InvalidOperationException("Combat state is outside bounded limits.");
        foreach (var candidate in state.Candidates)
        {
            RequireFinite(candidate.Position, nameof(candidate.Position));
            if (candidate.StableEntityId <= 0 || !double.IsFinite(candidate.HealthFraction) ||
                candidate.HealthFraction is < 0 or > 1 || !double.IsFinite(candidate.Distance) ||
                candidate.Distance < 0 || !double.IsFinite(candidate.AngularErrorDegrees))
                throw new InvalidOperationException("Combat candidate is outside bounded limits.");
        }
        return state;
    }

    private static void RequireFinite(System.Numerics.Vector3 value, string name)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidOperationException($"{name} contains a non-finite coordinate.");
    }

    private static bool SameProofBoundary(OfflineServerProof left, OfflineServerProof right) =>
        left.Kind == right.Kind &&
        left.Offline == right.Offline &&
        string.Equals(left.AuthMode, right.AuthMode, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ServerEndpoint, right.ServerEndpoint, StringComparison.Ordinal) &&
        string.Equals(left.DockerProject, right.DockerProject, StringComparison.Ordinal) &&
        string.Equals(left.DockerContainer, right.DockerContainer, StringComparison.Ordinal) &&
        string.Equals(left.DockerNetwork, right.DockerNetwork, StringComparison.Ordinal) &&
        FixedEquals(left.ServerSha256, right.ServerSha256) &&
        FixedEquals(left.ObserverNonce, right.ObserverNonce) &&
        SameLauncherEvidence(left.Launcher, right.Launcher);

    private static bool SameLauncherEvidence(LauncherSingleplayerEvidence? left, LauncherSingleplayerEvidence? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return left == right;
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private string Correlation(string action) => $"{action}-{Guid.NewGuid():N}";

    private void ResetRuntime()
    {
        heartbeatCancellation?.Dispose();
        heartbeatCancellation = null;
        heartbeatTask = null;
        client = null;
        proofProvider?.Dispose();
        proofProvider = null;
        lease = null;
        pinnedProof = null;
        rollingCaptureId = null;
        activeArtifactDirectory = null;
    }

    public async ValueTask DisposeAsync()
    {
        try { await DetachAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            lifecycleGate.Dispose();
            actionGate.Dispose();
            heartbeatCancellation?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
