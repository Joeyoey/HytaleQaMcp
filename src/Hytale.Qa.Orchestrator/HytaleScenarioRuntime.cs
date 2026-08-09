using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Orchestrator;

public interface IHytaleScenarioRuntimeFactory
{
    IQaScenarioRuntime Create(QaScenario scenario, int clientProcessId, string launcherEvidenceFileName, string artifactDirectory);
}

public sealed record QaLifecycleHandoffRequest(
    string Operation,
    int ClientProcessId,
    string PriorEvidenceFileName,
    string PriorEvidenceId,
    string SessionId,
    string WorldId,
    string WorldPathSha256,
    int ServerProcessId,
    DateTimeOffset RequestedAtUtc);

public sealed record QaLifecycleHandoffSubmission(
    int ClientProcessId,
    string EvidenceFileName,
    DateTimeOffset SubmittedAtUtc);

public interface IQaLifecycleHandoffRuntime
{
    void ConfigureLifecycleHandoff(
        Func<QaLifecycleHandoffRequest, CancellationToken, Task<QaLifecycleHandoffSubmission>> handoff);
}

public sealed class HytaleScenarioRuntimeFactory(
    LauncherSingleplayerProofProviderFactory proofs,
    IWorkerControlService worker,
    AuthenticatedObserverControls controls) : IHytaleScenarioRuntimeFactory
{
    public IQaScenarioRuntime Create(QaScenario scenario, int clientProcessId, string launcherEvidenceFileName,
        string artifactDirectory) => new HytaleScenarioRuntime(scenario, clientProcessId, launcherEvidenceFileName,
        artifactDirectory, proofs, worker, controls);
}

public sealed class HytaleScenarioRuntime : IQaScenarioRuntime, IQaLifecycleHandoffRuntime
{
    private readonly QaScenario scenario;
    private readonly int clientProcessId;
    private string evidenceFileName;
    private readonly string artifacts;
    private readonly LauncherSingleplayerProofProviderFactory proofFactory;
    private readonly IWorkerControlService worker;
    private readonly AuthenticatedObserverControls controls;
    private LauncherSingleplayerProofProvider? preflight;
    private OfflineServerProof? pinnedProof;
    private ObserverObservation? lastObservation;
    private string? launchScreenshotSha256;
    private int screenshotSequence;
    private string? rollingCaptureId;
    private string? audioCaptureId;
    private Guid? proofBoundPlayerId;
    private bool? clientModalVisible;
    private JsonElement? previousEvidenceSnapshot;
    private long lastPersistedObservationSequence = -1;
    private readonly HashSet<string> observedRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObserverFixturePins fixturePins = new();
    private Func<QaLifecycleHandoffRequest, CancellationToken, Task<QaLifecycleHandoffSubmission>>? lifecycleHandoff;

    public HytaleScenarioRuntime(QaScenario scenario, int clientProcessId, string evidenceFileName,
        string artifactDirectory, LauncherSingleplayerProofProviderFactory proofFactory,
        IWorkerControlService worker, AuthenticatedObserverControls controls)
    {
        this.scenario = scenario;
        this.clientProcessId = clientProcessId;
        this.evidenceFileName = evidenceFileName;
        artifacts = Path.GetFullPath(artifactDirectory);
        this.proofFactory = proofFactory;
        this.worker = worker;
        this.controls = controls;
    }

    public void ConfigureLifecycleHandoff(
        Func<QaLifecycleHandoffRequest, CancellationToken, Task<QaLifecycleHandoffSubmission>> handoff) =>
        lifecycleHandoff = handoff ?? throw new ArgumentNullException(nameof(handoff));

    public async Task<QaSafetySnapshot> SafetyAsync(CancellationToken cancellationToken)
    {
        var proofCurrent = false;
        var observerCurrent = false;
        var packetValid = false;
        bool? modal = null;
        var dead = false;
        var heldInputCount = 0;
        try
        {
            if (worker.State.Phase == WorkerControlPhase.Armed)
            {
                await worker.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
                var workerSafety = await worker.SafetyAsync(requireForeground: false, cancellationToken).ConfigureAwait(false);
                if (!workerSafety.Safe) throw new InvalidOperationException($"{workerSafety.Code}: {workerSafety.Message}");
                heldInputCount = workerSafety.HeldInputCount;
                lastObservation = await ObserveWithTransientRetryAsync(
                    worker.ObserveSolePlayerAsync, cancellationToken).ConfigureAwait(false);
                proofCurrent = true;
            }
            else if (preflight is not null)
            {
                var proof = await preflight.GetFreshProofAsync(cancellationToken).ConfigureAwait(false);
                proofCurrent = SameWorldBoundary(proof, pinnedProof ?? proof);
                lastObservation = await ObserveWithTransientRetryAsync(
                    preflight.ObserveSolePlayerAsync, cancellationToken).ConfigureAwait(false);
            }
            if (lastObservation is not null)
            {
                observerCurrent = DateTimeOffset.UtcNow - lastObservation.ObservedAt <= TimeSpan.FromSeconds(3);
                packetValid = lastObservation.EvidenceValid;
                var state = lastObservation.WorldSnapshot.GetProperty("state");
                dead = OptionalBoolean(state, "dead");
                modal = clientModalVisible ?? (state.TryGetProperty("modalVisible", out var modalValue) &&
                    modalValue.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? modalValue.GetBoolean()
                    : null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (ObserverBindingDriftException) { proofCurrent = observerCurrent = packetValid = false; }
        catch { proofCurrent = observerCurrent = packetValid = false; }

        var clients = Process.GetProcessesByName("HytaleClient");
        try
        {
            var matching = clients.SingleOrDefault(value => value.Id == clientProcessId);
            DateTimeOffset? started = matching is null ? null : matching.StartTime.ToUniversalTime();
            var foreground = matching is not null && matching.MainWindowHandle != nint.Zero &&
                GetForegroundWindow() == matching.MainWindowHandle;
            return new(proofCurrent, true, proofCurrent, observerCurrent, packetValid, clients.Length,
                matching?.Id, started, foreground, proofCurrent, modal, dead, heldInputCount);
        }
        finally { foreach (var process in clients) process.Dispose(); }
    }

    public async Task<QaStepResult> ExecuteAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        try
        {
            var result = step.Operation switch
            {
                "session.start" => await StartAsync(cancellationToken).ConfigureAwait(false),
                "client.launch_offline" => await AttachAsync(cancellationToken).ConfigureAwait(false),
                "session.stop" => await StopAsync(step, cancellationToken).ConfigureAwait(false),
                "wait.for" => await WaitAsync(step, cancellationToken).ConfigureAwait(false),
                "navigate.to" => await NavigateAsync(step, cancellationToken).ConfigureAwait(false),
                "interact.with" or "route.choose" => await InteractAsync(step, cancellationToken).ConfigureAwait(false),
                "combat.clear" => await CombatClearAsync(step, cancellationToken).ConfigureAwait(false),
                "combat.use_ability" => await AbilityAsync(step, cancellationToken).ConfigureAwait(false),
                "trace.mark" => await TraceMarkAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.state" => await AssertStateAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.ui" => await AssertUiAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.visual" => await AssertVisualAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.correlated" => await AssertCorrelatedAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.count" => await AssertCountAsync(step, cancellationToken).ConfigureAwait(false),
                "assert.hash_chain" => await AssertHashChainAsync(step, cancellationToken).ConfigureAwait(false),
                var operation when operation.StartsWith("fault.", StringComparison.Ordinal) => Blocked("fault-operation-disabled",
                    "Fault injection is outside the physical runtime and was not executed."),
                "inventory.inspect" => await InspectInventoryAsync(step, cancellationToken).ConfigureAwait(false),
                "ui.activate" => await ActivateUiAsync(step, cancellationToken).ConfigureAwait(false),
                "inventory.equip" or "inventory.move" or "inventory.reroll" or
                    "inventory.salvage" or "inventory.recover" =>
                    await MutateInventoryThroughUiAsync(step, cancellationToken).ConfigureAwait(false),
                var operation when operation.StartsWith("ui.", StringComparison.Ordinal) ||
                    operation.StartsWith("inventory.", StringComparison.Ordinal) =>
                    Untested("semantic-operation-unimplemented",
                        $"Operation '{operation}' is not implemented by the semantic UI adapter."),
                var operation when operation.Contains("audio", StringComparison.OrdinalIgnoreCase) =>
                    Untested("audio-assertion-unavailable", "Process audio can be captured, but this runtime has no objective cue classifier."),
                "client.reconnect" or "server.restart" =>
                    await LifecycleHandoffAsync(step, cancellationToken).ConfigureAwait(false),
                _ => Untested("operation-unsupported", $"Operation '{step.Operation}' has no truthful physical runtime adapter.")
            };
            if (step.Operation is not "session.start" and not "session.stop" &&
                worker.State.Phase == WorkerControlPhase.Armed &&
                scenario.Artifacts.TryGetProperty("screenshots", out var screenshotMode) &&
                string.Equals(screenshotMode.GetString(), "all_actions", StringComparison.Ordinal))
                _ = await worker.ScreenshotAsync(NextScreenshot($"step-{step.Id}"), cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (ObserverBindingDriftException failure)
        { return new(false, failure.Code, failure.Message, false, failure.Outcome); }
        catch (InvalidOperationException failure) when (failure.Message.StartsWith("observer-", StringComparison.Ordinal))
        { return Blocked(failure.Message, "Authenticated observer could not resolve the requested target."); }
    }

    public async Task ReleaseAllInputsAsync(CancellationToken cancellationToken)
    {
        if (worker.State.Phase == WorkerControlPhase.Armed)
            await worker.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<QaStepResult> StartAsync(CancellationToken cancellationToken)
    {
        var supportedCapabilities = new HashSet<string>(
            ["physical_input", "observer_guidance", "combat_reflex", "deterministic_unstuck"],
            StringComparer.Ordinal);
        var unsupported = scenario.Capabilities.Where(capability => !supportedCapabilities.Contains(capability))
            .OrderBy(value => value, StringComparer.Ordinal).ToArray();
        if (unsupported.Length > 0 || scenario.Mode == "white_box")
            return Blocked("unsupported-scenario-capabilities",
                $"Launcher physical runtime cannot safely provide: {string.Join(", ", unsupported.DefaultIfEmpty(scenario.Mode))}.");
        preflight = proofFactory.Create(evidenceFileName);
        pinnedProof = await preflight.GetFreshProofAsync(cancellationToken).ConfigureAwait(false);
        if (preflight.ClientProcessId != clientProcessId)
            return Blocked("launcher-client-pid-mismatch", "Requested client PID does not match verified launch evidence.");
        lastObservation = await preflight.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        if (!LauncherWorldMatches(lastObservation.WorldSnapshot,
                pinnedProof.Launcher?.WorldId ?? ""))
            return Blocked("launcher-world-mismatch",
                "Observer is neither in the exact launcher root world nor an authenticated active-run instance owned by it.");
        var snapshot = lastObservation.WorldSnapshot;
        var binding = ObserverFixtureBinding.Validate(pinnedProof.Kind, scenario.Fixture, snapshot, fixturePins);
        if (!binding.Valid)
            return new(false, binding.Code, binding.Message, false, binding.Outcome);
        proofBoundPlayerId = binding.PlayerId;
        return Pass("offline-launcher-session-proven",
            "Launcher-owned offline singleplayer proof, proof-bound sole player, and actual fixture provenance are current; no gameplay input was attached.");
    }

    private async Task<QaStepResult> AttachAsync(CancellationToken cancellationToken)
    {
        if (pinnedProof is null) return Blocked("session-not-proven", "session.start must prove launcher-owned offline state first.");
        var combat = scenario.Mode == "guided_physical" && scenario.Capabilities.Contains("combat_reflex");
        var capabilities = new EvidenceCapabilities(true, true, combat, false, false, false);
        await worker.AttachLauncherAsync(clientProcessId, evidenceFileName,
            scenario.Mode == "black_box" ? AssistanceMode.BlackBox : AssistanceMode.GuidedPhysical,
            capabilities, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(artifacts);
        var capture = await worker.ScreenshotAsync(NextScreenshot("launch"), cancellationToken).ConfigureAwait(false);
        if (capture.Width != scenario.Fixture.Client.Width || capture.Height != scenario.Fixture.Client.Height ||
            !string.Equals(capture.DisplayMode, scenario.Fixture.Client.DisplayMode, StringComparison.Ordinal) ||
            Math.Abs(scenario.Fixture.Client.HudScale - 1d) > 0.0001)
            return Blocked("semantic-ui-client-profile-mismatch",
                $"Captured client is {capture.Width}x{capture.Height} {capture.DisplayMode}; scenario requires " +
                $"{scenario.Fixture.Client.Width}x{scenario.Fixture.Client.Height} " +
                $"{scenario.Fixture.Client.DisplayMode} at HUD scale 1.");
        launchScreenshotSha256 = capture.Sha256;
        var surface = GameplaySurfaceClassifier.Classify(capture.Path);
        clientModalVisible = surface.State == GameplaySurfaceState.Ready ? false : null;
        if (surface.State != GameplaySurfaceState.Ready)
            return Blocked(surface.Code, surface.Message);
        if (scenario.Artifacts.TryGetProperty("rollingVideoSeconds", out var rollingSeconds) &&
            rollingSeconds.TryGetInt32(out var seconds) && seconds > 0)
        {
            var rolling = await worker.RollingStartAsync($"{Sanitize(Path.GetFileName(artifacts))}-{Sanitize(scenario.Id)}-rolling", 2,
                Math.Clamp(seconds, 1, 300), encodeMp4: false, cancellationToken).ConfigureAwait(false);
            rollingCaptureId = rolling.GetProperty("captureId").GetString();
        }
        if (scenario.Artifacts.TryGetProperty("audio", out var audio) && audio.GetBoolean())
        {
            var state = await worker.AudioStartAsync(
                $"{Sanitize(Path.GetFileName(artifacts))}-{Sanitize(scenario.Id)}-audio.wav",
                cancellationToken).ConfigureAwait(false);
            audioCaptureId = state.CaptureId;
            if (state.Status != AudioCaptureStatus.Capturing || string.IsNullOrWhiteSpace(audioCaptureId))
                return Untested("audio-capture-unavailable", state.Capability.Limitation ?? "Process audio capture did not start.");
        }
        return Pass("existing-client-attached", "Attached the existing launcher-proven client; no Hytale process was launched.");
    }

    private async Task<QaStepResult> StopAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var heldInputCount = 0;
        if (worker.State.Phase == WorkerControlPhase.Armed)
        {
            await worker.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
            var safety = await worker.SafetyAsync(requireForeground: false, cancellationToken).ConfigureAwait(false);
            heldInputCount = safety.HeldInputCount;
        }
        if (rollingCaptureId is not null && worker.State.Phase == WorkerControlPhase.Armed)
        {
            _ = await worker.RollingStopAsync(rollingCaptureId, cancellationToken).ConfigureAwait(false);
            rollingCaptureId = null;
        }
        if (audioCaptureId is not null && worker.State.Phase == WorkerControlPhase.Armed)
        {
            _ = await worker.AudioStopAsync(audioCaptureId, cancellationToken).ConfigureAwait(false);
            audioCaptureId = null;
        }
        if (worker.State.Phase is WorkerControlPhase.Armed or WorkerControlPhase.Faulted)
        {
            var detached = await worker.DetachAsync(cancellationToken).ConfigureAwait(false);
            if (detached.Phase != WorkerControlPhase.Detached)
                return new(false, "worker-detach-incomplete", "Worker did not reach its detached state.", false);
        }
        preflight?.Dispose();
        preflight = null;
        var clientCount = Process.GetProcessesByName("HytaleClient").Length;
        if (ExpectedInt(step.Expect, "clientCount") is { } expectedClients && expectedClients != clientCount)
            return Blocked("session-stop-client-contract-mismatch",
                $"Stop contract expected {expectedClients} Hytale clients but launcher-owned detach preserves {clientCount}.");
        if (ExpectedBoolean(step.Expect, "workerAttached") is { } expectedAttached &&
            expectedAttached != (worker.State.Phase != WorkerControlPhase.Detached))
            return Blocked("session-stop-worker-contract-mismatch", "Worker attachment state did not match the declared stop contract.");
        if (ExpectedInt(step.Expect, "heldInputCount") is { } expectedHeld && expectedHeld != heldInputCount)
            return new(false, "session-stop-held-input-mismatch",
                $"Stop contract expected {expectedHeld} held inputs but the worker reported {heldInputCount}.", false,
                QaRunOutcome.AbortedSafety);
        return Pass("launcher-session-evidence-finalized",
            "Rolling evidence was finalized and worker input was detached; the launcher-owned client remains running by design.");
    }

    private async Task<QaStepResult> LifecycleHandoffAsync(
        QaScenarioStep step,
        CancellationToken cancellationToken)
    {
        if (lifecycleHandoff is null)
            return Blocked("lifecycle-handoff-unavailable",
                "The coordinator did not provide a launcher-proof handoff channel.");
        if (pinnedProof?.Launcher is not { } priorLauncher)
            return Blocked("lifecycle-prior-proof-unavailable",
                "Reconnect/restart requires a pinned launcher-owned proof.");
        var before = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var priorSessionId = preflight?.SessionId
            ?? throw new InvalidOperationException("lifecycle-session-id-unavailable");
        if (worker.State.Phase == WorkerControlPhase.Armed)
        {
            await worker.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
            if (rollingCaptureId is not null)
            {
                _ = await worker.RollingStopAsync(rollingCaptureId, cancellationToken).ConfigureAwait(false);
                rollingCaptureId = null;
            }
            if (audioCaptureId is not null)
            {
                _ = await worker.AudioStopAsync(audioCaptureId, cancellationToken).ConfigureAwait(false);
                audioCaptureId = null;
            }
            _ = await worker.DetachAsync(cancellationToken).ConfigureAwait(false);
        }
        preflight?.Dispose();
        preflight = null;
        var request = new QaLifecycleHandoffRequest(
            step.Operation,
            clientProcessId,
            evidenceFileName,
            priorLauncher.EvidenceId,
            priorSessionId,
            priorLauncher.WorldId,
            priorLauncher.WorldPathSha256,
            priorLauncher.Server.ProcessId,
            DateTimeOffset.UtcNow);
        var submission = await lifecycleHandoff(request, cancellationToken).ConfigureAwait(false);
        if (submission.ClientProcessId != clientProcessId)
            return new(false, "lifecycle-client-pid-changed",
                "The renewed proof changed the one allowed Hytale client PID.", false,
                QaRunOutcome.AbortedSafety);

        var renewedProvider = proofFactory.Create(submission.EvidenceFileName);
        OfflineServerProof renewed;
        try { renewed = await renewedProvider.GetFreshProofAsync(cancellationToken).ConfigureAwait(false); }
        catch { renewedProvider.Dispose(); throw; }
        if (renewed.Launcher is not { } nextLauncher || renewedProvider.ClientProcessId != clientProcessId)
        {
            renewedProvider.Dispose();
            return new(false, "lifecycle-renewed-proof-invalid",
                "The renewed proof does not bind the same client.", false, QaRunOutcome.InvalidEvidence);
        }
        var sameClient = priorLauncher.Client.ProcessId == nextLauncher.Client.ProcessId &&
                         priorLauncher.Client.CreationTimeUtc == nextLauncher.Client.CreationTimeUtc &&
                         string.Equals(priorLauncher.Client.ExecutablePath, nextLauncher.Client.ExecutablePath,
                             StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(priorLauncher.Client.Sha256, nextLauncher.Client.Sha256,
                             StringComparison.OrdinalIgnoreCase);
        var sameWorld = string.Equals(priorLauncher.WorldId, nextLauncher.WorldId,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(priorLauncher.WorldPathSha256, nextLauncher.WorldPathSha256,
                            StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(pinnedProof.ServerEndpoint, renewed.ServerEndpoint,
                            StringComparison.OrdinalIgnoreCase);
        var freshEvidence = !string.Equals(priorLauncher.EvidenceId, nextLauncher.EvidenceId,
                                StringComparison.Ordinal) &&
                            !string.Equals(evidenceFileName, submission.EvidenceFileName,
                                StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(priorLauncher.LaunchEvidenceSha256,
                                nextLauncher.LaunchEvidenceSha256, StringComparison.OrdinalIgnoreCase);
        var serverRule = step.Operation == "server.restart"
            ? priorLauncher.Server.ProcessId != nextLauncher.Server.ProcessId &&
              priorLauncher.Server.CreationTimeUtc != nextLauncher.Server.CreationTimeUtc
            : true;
        if (!sameClient || !sameWorld || !freshEvidence || !serverRule)
        {
            renewedProvider.Dispose();
            return new(false, "lifecycle-continuity-proof-failed",
                "Renewed proof did not preserve client/world identity, rotate evidence, or satisfy the server restart rule.",
                false, QaRunOutcome.InvalidEvidence);
        }

        evidenceFileName = submission.EvidenceFileName;
        preflight = renewedProvider;
        pinnedProof = renewed;
        lastObservation = ValidateBoundObservation(
            await preflight.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false));
        var afterSnapshot = lastObservation.WorldSnapshot;
        if (afterSnapshot.GetProperty("state").GetRawText() == before.WorldSnapshot.GetProperty("state").GetRawText())
            return new(false, "lifecycle-observer-epoch-unchanged",
                "Renewed proof did not produce a distinct authenticated observer state.", false,
                QaRunOutcome.InvalidEvidence);
        var attached = await AttachAsync(cancellationToken).ConfigureAwait(false);
        if (!attached.Passed) return attached;
        var verification = VerifyLifecycleExpectations(step, before.WorldSnapshot,
            lastObservation.WorldSnapshot, priorLauncher, nextLauncher);
        return verification ?? Pass("lifecycle-handoff-complete",
            "Input was released, a renewed launcher-owned OFFLINE proof was independently validated, and the same client/world resumed.");
    }

    private static QaStepResult? VerifyLifecycleExpectations(
        QaScenarioStep step,
        JsonElement before,
        JsonElement after,
        LauncherSingleplayerEvidence prior,
        LauncherSingleplayerEvidence renewed)
    {
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expected) return null;
        var beforeState = before.GetProperty("state");
        var afterState = after.GetProperty("state");
        foreach (var property in expected.EnumerateObject())
        {
            bool? special = property.Name switch
            {
                "sameClientPid" => prior.Client.ProcessId == renewed.Client.ProcessId,
                "sameRunId" => SameField(beforeState, afterState, "runId"),
                "sameRunGeometryHash" => SameField(beforeState, afterState, "runGeometryHash"),
                "allNativeLocationsPreserved" => SameField(beforeState, afterState, "nativeInventory"),
                _ => null
            };
            if (special is { } result)
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    property.Value.GetBoolean() != result)
                    return new(false, "lifecycle-expectation-mismatch",
                        $"Lifecycle expectation '{property.Name}' did not match.", true);
                continue;
            }
            if (!TryResolveAssertValue(afterState, property.Name, out var actual))
                return Untested("lifecycle-expectation-field-unavailable",
                    $"Authenticated resumed state does not expose '{property.Name}'.");
            if (!JsonSubset(actual, property.Value))
                return new(false, "lifecycle-expectation-mismatch",
                    $"Lifecycle expectation '{property.Name}' did not match.", true);
        }
        return null;
    }

    private static bool SameField(JsonElement before, JsonElement after, string name) =>
        before.TryGetProperty(name, out var left) && after.TryGetProperty(name, out var right) &&
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private async Task<QaStepResult> WaitAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var (kind, id, _) = Target(step);
        var targetState = TargetState(step);
        while (true)
        {
            lastObservation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            var state = lastObservation.WorldSnapshot.GetProperty("state");
            if (kind == "objective" && id == "world.ready" && OptionalBoolean(state, "connected"))
                return Pass("world-ready", "Authenticated sole-player snapshot reports a connected world.");
            if (kind == "objective" && ObjectivePresent(state, id))
                return Pass("objective-observed", "Requested objective is present in authenticated run state.");
            if (kind == "gate")
            {
                try
                {
                    _ = ObservedState.Parse(lastObservation).ResolveTarget(kind, id, targetState);
                    return Pass("gate-state-observed", "Requested gate identity and lifecycle state are present in authenticated world state.");
                }
                catch (InvalidOperationException failure) when (
                    string.Equals(failure.Message, "observer-gate-target-unavailable", StringComparison.Ordinal))
                {
                    // The requested lifecycle state has not arrived yet.
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<QaStepResult> NavigateAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var (kind, id, within) = Target(step);
        var targetState = TargetState(step);
        if (kind == "entity") id = TargetRole(step) ?? id;
        while (true)
        {
            var result = await controls.NavigateAsync(kind, id, targetState, within, cancellationToken).ConfigureAwait(false);
            if (result.ObjectiveReached) return Pass("navigation-target-reached", result.Message);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<QaStepResult> InteractAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var (kind, id, _) = Target(step);
        var targetState = TargetState(step);
        if (kind == "entity") id = TargetRole(step) ?? id;
        var before = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        _ = await controls.InteractAsync(kind, id, targetState, cancellationToken).ConfigureAwait(false);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var after = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        if (step.Operation == "route.choose" && step.Expect is { } expected && expected.TryGetProperty("route", out var route))
        {
            var selected = after.WorldSnapshot.GetProperty("state").TryGetProperty("selectedRoute", out var value)
                ? value.GetString() : null;
            return string.Equals(selected, route.GetString(), StringComparison.OrdinalIgnoreCase)
                ? Pass("route-choice-observed", "Authoritative selectedRoute matches the physical interaction.")
                : new(false, "route-choice-not-observed", "Physical interaction did not produce the expected authoritative route.", false);
        }
        var beforeState = before.WorldSnapshot.GetProperty("state");
        var afterState = after.WorldSnapshot.GetProperty("state");
        if (kind == "gate")
        {
            var expectedPage = ExpectedString(step.Expect, "uiOpened");
            if (!string.IsNullOrWhiteSpace(expectedPage))
                return SemanticUiPageMatches(afterState, expectedPage)
                    ? Pass("gate-inspection-observed",
                        "Physical gate use opened the expected authenticated player-facing page.")
                    : new(false, "gate-inspection-not-observed",
                        "Physical gate use did not open the expected authenticated player-facing page.", false);
            var expectedAcceptance = ExpectedBoolean(step.Expect, "admissionAccepted") ??
                ExpectedBoolean(step.Expect, "physicalUse") ??
                (ExpectedString(step.Expect, "event") == "gate.admission_accepted" ? true : null);
            if (expectedAcceptance == false)
                return Untested("gate-denial-ledger-unavailable",
                    "The physical click was sent, but the observer exposes no authenticated denial event; unchanged state cannot prove rejection.");
            var admitted = !OptionalBoolean(beforeState, "runAttached") && OptionalBoolean(afterState, "runAttached");
            return admitted
                ? Pass("gate-admission-observed", "Physical gate use produced an authoritative detached-to-attached run transition.")
                : new(false, "gate-admission-not-observed", "Physical gate use did not produce the expected authoritative run transition.", false);
        }
        if (AuthoritativeInteractionChanged(beforeState, afterState))
            return Pass("interaction-effect-observed", "Physical interaction changed target-relevant authoritative objective state.");
        return Untested("interaction-effect-unproven",
            "A fresh observation alone cannot prove that this target handled the physical interaction; no target-relevant authoritative state changed.");
    }

    private async Task<QaStepResult> CombatClearAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var role = TargetRole(step);
        var seenAlive = new HashSet<long>();
        var seenDead = new HashSet<long>();
        while (true)
        {
            var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            var state = ObservedState.Parse(observation);
            foreach (var encounter in state.Encounters.Where(value => !string.IsNullOrWhiteSpace(value.Role)))
                observedRoles.Add(encounter.Role);
            var matching = state.Encounters.Where(value => AuthenticatedObserverControls.IsAttackableEncounter(value) &&
                (role is null || AuthenticatedObserverControls.RoleMatches(value.Role, role))).ToArray();
            foreach (var encounter in matching.Where(value => value.Alive)) seenAlive.Add(encounter.StableEntityId);
            foreach (var encounter in matching.Where(value => !value.Alive && seenAlive.Contains(value.StableEntityId)))
                seenDead.Add(encounter.StableEntityId);
            var matchingAlive = matching.Any(value => value.Alive);
            if (!matchingAlive)
            {
                if (seenAlive.Count == 0)
                    return Blocked("observer-encounter-never-spawned",
                        "No matching authenticated current-run hostile spawn was observed.");
                if (!seenAlive.All(seenDead.Contains))
                    return Untested("authoritative-death-evidence-missing",
                        "A previously alive hostile disappeared without an authenticated dead transition; absence is not accepted as kill proof.");
                if (step.Expect is { } expect && expect.EnumerateObject().Any(property => property.Name is not "authoritativeDeath" and not "directDamage"))
                    return Untested("combat-postcondition-evidence-unavailable",
                        "Authoritative deaths were observed, but one or more declared combat postconditions are not exposed by this adapter.");
                return Pass("combat-deaths-observed", "Every matching current-run hostile was observed alive and then authoritatively dead.");
            }
            var result = await controls.CombatAsync(role, cancellationToken).ConfigureAwait(false);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<QaStepResult> AbilityAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var role = TargetRole(step);
        if (role is not null)
        {
            var targetObservation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            var targetState = ObservedState.Parse(targetObservation);
            if (!targetState.Encounters.Any(value => AuthenticatedObserverControls.IsAttackableEncounter(value) &&
                    AuthenticatedObserverControls.RoleMatches(value.Role, role)))
                return Blocked("observer-ability-target-not-found", "Requested ability target role is absent from authenticated hostile current-run encounters.");
            _ = await controls.AimAttackAsync(role, cancellationToken).ConfigureAwait(false);
        }
        var input = step.Arguments is { } arguments && arguments.TryGetProperty("input", out var value)
            ? value.GetString() ?? "" : "";
        var before = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var beforeState = before.WorldSnapshot.GetProperty("state");
        var counter = OptionalLong(beforeState, "combatActionCounter");
        await worker.UseAbilityAsync(input, cancellationToken).ConfigureAwait(false);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var after = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var afterCounter = OptionalLong(after.WorldSnapshot.GetProperty("state"), "combatActionCounter");
        var expectedAccepted = step.Expect is { } expect && expect.TryGetProperty("accepted", out var accepted)
            ? accepted.GetBoolean() : (bool?)null;
        if (expectedAccepted is null) return Untested("ability-acceptance-unspecified", "Ability input was sent but no acceptance expectation was declared.");
        var changed = afterCounter > counter;
        return changed == expectedAccepted.Value
            ? Pass("ability-result-observed", "Authoritative combat action counter matches the expected acceptance result.")
            : new(false, "ability-result-mismatch", "Authoritative combat action counter did not match the expected result.", true);
    }

    private async Task<QaStepResult> TraceMarkAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var probe = step.Arguments is { ValueKind: JsonValueKind.Object } arguments &&
            arguments.TryGetProperty("physicalInputProbe", out var value)
            ? value.GetString() : null;
        if (!string.Equals(probe, "look_and_click", StringComparison.Ordinal))
            return Untested("trace-marker-unsupported",
                "This physical runtime only supports the authenticated look-and-click trace probe.");

        var (kind, id, _) = Target(step);
        var targetState = TargetState(step);
        if (kind is not ("gate" or "objective" or "fallback" or "entity"))
            return Blocked("observer-trace-target-unsupported",
                "The trace probe requires a server-authored semantic target; caller coordinates were not accepted.");
        if (kind == "entity") id = TargetRole(step) ?? id;

        var before = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = ObservedState.Parse(before);
        var target = state.ResolveTarget(kind, id, targetState).Position;
        var aim = await worker.AimStepAsync(new(state.Eye, state.YawDegrees, state.PitchDegrees, target),
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        var afterAim = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var click = await worker.InteractAsync("left_click", cancellationToken).ConfigureAwait(false);
        await worker.ReleaseAllAsync(cancellationToken).ConfigureAwait(false);
        var safety = await worker.SafetyAsync(requireForeground: true, cancellationToken).ConfigureAwait(false);

        var lookIssued = aim.Intents.Any(intent => intent.Kind == PhysicalIntentKind.Look &&
            (Math.Abs(intent.X) > 0 || Math.Abs(intent.Y) > 0));
        var clickIssued = click.Intents.Count == 1 &&
            click.Intents[0].Kind == PhysicalIntentKind.PrimaryClick;
        var observationAdvanced = afterAim.ObservationSequence > before.ObservationSequence;
        if (!lookIssued)
            return Untested("physical-look-probe-degenerate",
                "The authenticated target was already aligned with the crosshair, so this run could not prove non-zero look input.");
        if (!clickIssued || !observationAdvanced || !safety.Safe || safety.HeldInputCount != 0)
            return new(false, "physical-input-probe-mismatch",
                "The worker did not prove the complete look, observation, click, and release sequence.", false,
                QaRunOutcome.AbortedSafety);
        return Pass("physical-input-probe-observed",
            $"Issued proof-gated look and click input toward authenticated {kind} target; observer sequence advanced from {before.ObservationSequence} to {afterAim.ObservationSequence} and no input remained held.");
    }

    private async Task<QaStepResult> AssertStateAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        if (step.Expect is null) return Untested("assertion-empty", "State assertion has no expected object.");
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        foreach (var expected in step.Expect.Value.EnumerateObject())
            if (!TryResolveAssertValue(state, expected.Name, out var actual))
                return Untested("state-field-unavailable", $"Authenticated observer does not expose or derive '{expected.Name}'.");
            else if (!JsonSubset(actual, expected.Value))
                return new(false, "state-assertion-mismatch", $"Observed '{expected.Name}' does not match the declared value.", true);
        return Pass("state-assertion-observed", "Every declared state field matched authenticated observer data.");
    }

    private async Task<QaStepResult> AssertCountAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expect ||
            !expect.TryGetProperty("event", out var eventName) || eventName.ValueKind != JsonValueKind.String ||
            !expect.TryGetProperty("equals", out var equals) || !equals.TryGetInt32(out var expectedCount))
            return Untested("event-count-contract-invalid", "assert.count requires string event and integer equals fields.");
        var evidence = await ObserverEvidenceAsync(cancellationToken).ConfigureAwait(false);
        if (evidence is null) return Untested("observer-event-ledger-unavailable",
            "The authenticated observer does not expose qaEvidence.events.");
        if (!EvidenceSupports(evidence.Value, "supportedEventTypes", eventName.GetString()!))
            return Untested("event-type-not-instrumented",
                $"The authenticated observer does not instrument event '{eventName.GetString()}'.");
        var events = evidence.Value.GetProperty("events");
        var filters = expect.EnumerateObject().Where(property => property.Name is not "event" and not "equals").ToArray();
        var actual = events.EnumerateArray().Count(item =>
            EventName(item) == eventName.GetString() && filters.All(filter => EventFieldMatches(item, filter)));
        return actual == expectedCount
            ? Pass("event-count-observed", $"Authenticated event ledger contains exactly {actual} matching event(s).")
            : new(false, "event-count-mismatch", $"Expected {expectedCount} matching events but observed {actual}.", true);
    }

    private async Task<QaStepResult> AssertCorrelatedAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expect ||
            !expect.TryGetProperty("events", out var expectedEvents) || expectedEvents.ValueKind != JsonValueKind.Array)
            return Untested("event-correlation-contract-invalid", "assert.correlated requires an events array.");
        var evidence = await ObserverEvidenceAsync(cancellationToken).ConfigureAwait(false);
        if (evidence is null) return Untested("observer-event-ledger-unavailable",
            "The authenticated observer does not expose qaEvidence.events.");
        var requested = expectedEvents.EnumerateArray().Select(value => value.GetString() ?? "").ToArray();
        var unsupported = requested.Where(name =>
            !EvidenceSupports(evidence.Value, "supportedEventTypes", name)).ToArray();
        if (unsupported.Length > 0)
            return Untested("event-type-not-instrumented",
                $"The authenticated observer does not instrument: {string.Join(", ", unsupported)}.");
        var observed = evidence.Value.GetProperty("events").EnumerateArray().Select((value, index) => new
        {
            Value = value,
            Index = index,
            Name = EventName(value),
            At = EventTimestamp(value)
        }).ToArray();
        var selected = requested.Select(name => observed.FirstOrDefault(value => value.Name == name)).ToArray();
        if (selected.Any(value => value is null))
            return new(false, "correlated-event-missing", "One or more required events are absent from the authenticated ledger.", true);
        var concrete = selected.Select(value => value!).ToArray();
        if (expect.TryGetProperty("ordered", out var ordered) && ordered.GetBoolean() &&
            !concrete.Select(value => value.Index).SequenceEqual(concrete.Select(value => value.Index).Order()))
            return new(false, "correlated-event-order-mismatch", "Required events were not observed in declared order.", true);
        if (expect.TryGetProperty("maximumSkewMilliseconds", out var maximum) && maximum.TryGetInt64(out var maximumSkew))
        {
            if (concrete.Any(value => value.At is null))
                return Untested("event-timestamps-unavailable", "Event correlation requires authenticated timestamps.");
            var timestamps = concrete.Select(value => value.At!.Value).ToArray();
            if (timestamps.Max() - timestamps.Min() > maximumSkew)
                return new(false, "correlated-event-skew-mismatch", "Required events exceeded maximum declared skew.", true);
        }
        return Pass("correlated-events-observed", "Required authenticated events were present with valid ordering and skew.");
    }

    private async Task<QaStepResult> AssertHashChainAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expect ||
            !expect.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.String)
            return Untested("hash-chain-contract-invalid", "assert.hash_chain requires a subject.");
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        if (!TryQaEvidence(state, out var qaEvidence))
            return Untested("observer-hash-chain-unavailable",
                "The authenticated observer does not expose the requested qaEvidence.hashChains subject.");
        var subjectName = subject.GetString()!;
        if (!EvidenceSupports(qaEvidence, "supportedHashSubjects", subjectName))
            return Untested("hash-subject-not-instrumented",
                $"The authenticated observer does not instrument hash subject '{subjectName}'.");
        if (!TryHashEvidence(qaEvidence, subjectName, out var actual))
            return new(false, "hash-subject-missing",
                "A declared supported hash subject was absent from authenticated evidence.", true,
                QaRunOutcome.InvalidEvidence);
        var expected = JsonSerializer.SerializeToElement(expect.EnumerateObject()
            .Where(property => property.Name != "subject").ToDictionary(property => property.Name,
                property => property.Value.Clone(), StringComparer.Ordinal));
        var unavailable = expected.EnumerateObject().Where(property => !actual.TryGetProperty(property.Name, out _))
            .Select(property => property.Name).ToArray();
        if (unavailable.Length > 0)
            return Untested("hash-proof-field-unavailable",
                $"Authenticated hash evidence does not prove: {string.Join(", ", unavailable)}.");
        return JsonSubset(actual, expected)
            ? Pass("hash-chain-observed", "Authenticated hash-chain evidence matches every declared field.")
            : new(false, "hash-chain-mismatch", "Authenticated hash-chain evidence differs from the declared fields.", true);
    }

    private async Task<QaStepResult> InspectInventoryAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var (kind, id, _) = Target(step);
        if (kind != "inventory" || string.IsNullOrWhiteSpace(id))
            return Untested("inventory-selector-invalid", "inventory.inspect requires a semantic inventory target id.");
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        if (!state.TryGetProperty("nativeInventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object ||
            !OptionalBoolean(inventory, "available") || !inventory.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            return Untested("native-inventory-evidence-unavailable",
                "Authenticated nativeInventory.items evidence is unavailable.");
        var candidates = InventoryCandidates(inventory).ToList();
        var semanticContractAvailable = inventory.TryGetProperty("semanticSelectors", out var selectors) &&
                                        selectors.ValueKind == JsonValueKind.Object;
        var matches = candidates.Where(item => InventoryItemMatches(item, id)).ToArray();
        if (matches.Length == 0)
        {
            if (IsSemanticInventorySelector(id) && !semanticContractAvailable)
                return Untested("semantic-inventory-selector-unavailable",
                    "The authenticated observer has not published nativeInventory.semanticSelectors.");
            return new(false, "inventory-item-not-observed", "No current native item matched the semantic selector.", true);
        }
        if (matches.Length != 1)
            return new(false, "inventory-item-ambiguous", "More than one current native item matched the semantic selector.", true,
                QaRunOutcome.InvalidEvidence);
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expect)
            return Pass("inventory-item-observed", "Exactly one authenticated current native item matched.");
        foreach (var property in expect.EnumerateObject())
        {
            if (!TryInventoryExpectation(matches[0], property.Name, out var actual))
                return Untested("inventory-field-unavailable",
                    $"Authenticated inventory evidence does not expose or derive '{property.Name}'.");
            if (!JsonSubset(actual, property.Value))
                return new(false, "inventory-inspection-mismatch",
                    $"Authenticated inventory field '{property.Name}' did not match.", true);
        }
        return Pass("inventory-inspection-observed", "Every declared inventory property matched the exact signed native item.");
    }

    private async Task<QaStepResult> AssertUiAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        if (step.Expect is not { ValueKind: JsonValueKind.Object } expected)
            return Untested("ui-expectation-empty", "assert.ui requires an expectation object.");
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        if (!TrySemanticUi(state, out var ui))
            return Untested("semantic-ui-evidence-unavailable",
                "The authenticated observer did not publish semanticUi evidence.");
        if (!ui.TryGetProperty("assertions", out var assertions) || assertions.ValueKind != JsonValueKind.Object)
            return Untested("semantic-ui-assertions-unavailable",
                "The semantic UI projection does not publish objective assertions.");
        var missing = expected.EnumerateObject().Where(property =>
            !assertions.TryGetProperty(property.Name, out _)).Select(property => property.Name).ToArray();
        if (missing.Length > 0)
            return Untested("semantic-ui-assertion-field-unavailable",
                $"Authenticated UI evidence does not prove: {string.Join(", ", missing)}.");
        return JsonSubset(assertions, expected)
            ? Pass("semantic-ui-assertions-observed", "Every declared UI assertion matched authenticated evidence.")
            : new(false, "semantic-ui-assertion-mismatch",
                "Authenticated UI evidence differs from the declared assertions.", true);
    }

    private async Task<QaStepResult> ActivateUiAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        var (kind, id, _) = Target(step);
        if (kind != "ui" || string.IsNullOrWhiteSpace(id))
            return Untested("semantic-ui-selector-invalid", "ui.activate requires a semantic UI target id.");
        return await ExecuteSemanticUiNodeAsync(step, id, null, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<QaStepResult> MutateInventoryThroughUiAsync(
        QaScenarioStep step,
        CancellationToken cancellationToken)
    {
        var (kind, selector, _) = Target(step);
        if (kind != "inventory" || string.IsNullOrWhiteSpace(selector))
            return Untested("inventory-selector-invalid",
                $"{step.Operation} requires a semantic inventory target id.");
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        var subjectId = ResolveInventorySubjectId(state, selector);
        if (subjectId is null)
            return Untested("semantic-inventory-selector-unavailable",
                $"Authenticated inventory evidence could not resolve '{selector}' to exactly one current signed stack.");
        if (!TrySemanticUi(state, out var ui))
            return Untested("semantic-ui-evidence-unavailable",
                "Inventory mutations require an authenticated semanticUi projection from the player-facing page.");

        var selected = OptionalString(ui, "selectedSubjectId");
        if (!string.Equals(selected, subjectId, StringComparison.OrdinalIgnoreCase))
        {
            var select = await ExecuteSemanticUiNodeAsync(step, null, subjectId, "inventory.select",
                cancellationToken, verifyExpectations: false).ConfigureAwait(false);
            if (!select.Passed) return select;
            var selectedReady = await WaitForSemanticUiAsync(uiState =>
                string.Equals(OptionalString(uiState, "selectedSubjectId"), subjectId,
                    StringComparison.OrdinalIgnoreCase) && !OptionalBoolean(uiState, "busy"),
                cancellationToken).ConfigureAwait(false);
            if (!selectedReady)
                return new(false, "semantic-ui-selection-timeout",
                    "The player-facing page did not authenticate the requested selection before timeout.", false);
        }

        if (step.Operation == "inventory.reroll")
        {
            var affix = step.Arguments is { ValueKind: JsonValueKind.Object } args &&
                        args.TryGetProperty("affixIndex", out var value) && value.TryGetInt32(out var index)
                ? index : 0;
            var preview = await ExecuteSemanticUiNodeAsync(step, null, subjectId,
                $"inventory.reroll.preview:{affix}", cancellationToken, verifyExpectations: false)
                .ConfigureAwait(false);
            if (!preview.Passed) return preview;
            if (!await WaitForSemanticOperationNodeAsync(subjectId, "inventory.reroll.commit",
                    cancellationToken).ConfigureAwait(false))
                return new(false, "semantic-ui-preview-timeout",
                    "The reroll preview did not produce an enabled authenticated commit node.", false);
        }
        if (step.Operation == "inventory.salvage")
        {
            var current = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            var currentState = current.WorldSnapshot.GetProperty("state");
            if (!TrySemanticUi(currentState, out var currentUi))
                return Untested("semantic-ui-evidence-unavailable",
                    "Salvage confirmation requires authenticated semantic UI evidence.");
            var commitReady = SemanticUiNodes(currentUi).Any(node => node.Enabled &&
                string.Equals(node.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.Operation, "inventory.salvage.commit", StringComparison.Ordinal));
            if (!commitReady)
            {
                var arm = await ExecuteSemanticUiNodeAsync(step, null, subjectId,
                    "inventory.salvage.arm", cancellationToken, verifyExpectations: false)
                    .ConfigureAwait(false);
                if (!arm.Passed) return arm;
                if (!await WaitForSemanticOperationNodeAsync(subjectId, "inventory.salvage.commit",
                        cancellationToken).ConfigureAwait(false))
                    return new(false, "semantic-ui-salvage-arm-timeout",
                        "The first salvage click did not produce an authenticated confirmation node.", false);
            }
        }

        var operation = step.Operation switch
        {
            "inventory.equip" => "inventory.equip",
            "inventory.move" => "inventory.move",
            "inventory.reroll" => "inventory.reroll.commit",
            "inventory.salvage" => "inventory.salvage.commit",
            "inventory.recover" => "inventory.recover",
            _ => throw new InvalidOperationException("semantic-inventory-operation-not-allowlisted")
        };
        return await ExecuteSemanticUiNodeAsync(step, null, subjectId, operation, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<QaStepResult> ExecuteSemanticUiNodeAsync(
        QaScenarioStep step,
        string? id,
        string? subjectId,
        string? operation,
        CancellationToken cancellationToken,
        bool verifyExpectations = true)
    {
        var before = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var beforeState = before.WorldSnapshot.GetProperty("state");
        if (!TrySemanticUi(beforeState, out var beforeUi))
            return Untested("semantic-ui-evidence-unavailable",
                "The authenticated observer did not publish semanticUi evidence.");
        var nodes = SemanticUiNodes(beforeUi);
        var matches = nodes.Where(node => node.Visible && node.Enabled)
            .Where(node => id is null || string.Equals(node.Id, id, StringComparison.Ordinal))
            .Where(node => subjectId is null || string.Equals(node.SubjectId, subjectId,
                StringComparison.OrdinalIgnoreCase))
            .Where(node => operation is null || string.Equals(node.Operation, operation,
                StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
            return Untested("semantic-ui-target-unavailable",
                $"No enabled authenticated UI node matched id='{id}', subject='{subjectId}', operation='{operation}'.");
        if (matches.Length != 1)
            return new(false, "semantic-ui-target-ambiguous",
                "More than one authenticated UI node matched the requested action.", false,
                QaRunOutcome.InvalidEvidence);
        var node = matches[0];
        var beforeRevision = OptionalLong(beforeUi, "revision");
        await worker.ClickUiAsync(node, cancellationToken).ConfigureAwait(false);

        ObserverObservation after = before;
        JsonElement afterState = beforeState;
        JsonElement afterUi = beforeUi;
        var changed = false;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            after = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            afterState = after.WorldSnapshot.GetProperty("state");
            changed = SemanticUiChanged(beforeUi, afterState, out afterUi);
            if (changed && (!verifyExpectations || step.Expect is not { ValueKind: JsonValueKind.Object } ||
                            TryOperationEvidence(afterState, out _))) break;
        }
        if (!changed)
            return new(false, "semantic-ui-no-authoritative-change",
                "The physical click produced no authenticated UI or inventory state change.", false);
        if (!verifyExpectations || step.Expect is not { ValueKind: JsonValueKind.Object } expected)
            return Pass("semantic-ui-physical-action-observed",
                "A unique authenticated node was physically clicked and the observer revision advanced.");
        if (!TryOperationEvidence(afterState, out var evidence))
            return Untested("semantic-operation-evidence-unavailable",
                "The click changed authoritative state, but the observer did not publish operationEvidence for its declared result.");
        if (subjectId is not null &&
            (!evidence.TryGetProperty("subjectId", out var evidenceSubject) ||
             evidenceSubject.ValueKind != JsonValueKind.String ||
             !string.Equals(evidenceSubject.GetString(), subjectId, StringComparison.OrdinalIgnoreCase)))
            return new(false, "semantic-operation-subject-mismatch",
                "Post-operation evidence does not bind the exact selected signed item.", false,
                QaRunOutcome.InvalidEvidence);
        var missing = expected.EnumerateObject().Where(property => !evidence.TryGetProperty(property.Name, out _))
            .Select(property => property.Name).ToArray();
        if (missing.Length > 0)
            return Untested("semantic-operation-field-unavailable",
                $"Authenticated post-action evidence does not prove: {string.Join(", ", missing)}.");
        return JsonSubset(evidence, expected)
            ? Pass("semantic-operation-committed", "Physical UI input and authenticated post-operation evidence match.")
            : new(false, "semantic-operation-mismatch",
                "Authenticated post-operation evidence differs from the declared result.", true);
    }

    private static bool TrySemanticUi(JsonElement state, out JsonElement ui) =>
        state.TryGetProperty("semanticUi", out ui) && ui.ValueKind == JsonValueKind.Object &&
        OptionalBoolean(ui, "available") && ui.TryGetProperty("nodes", out var nodes) &&
        nodes.ValueKind == JsonValueKind.Array;

    internal static bool SemanticUiPageMatches(JsonElement state, string pageId) =>
        TrySemanticUi(state, out var ui) &&
        string.Equals(OptionalString(ui, "pageId"), pageId, StringComparison.Ordinal);

    internal static bool SemanticUiChanged(
        JsonElement beforeUi,
        JsonElement afterState,
        out JsonElement afterUi)
    {
        if (!TrySemanticUi(afterState, out afterUi))
            return true; // The authenticated page closed or was replaced.
        return OptionalLong(afterUi, "revision") > OptionalLong(beforeUi, "revision") ||
               !string.Equals(afterUi.GetRawText(), beforeUi.GetRawText(), StringComparison.Ordinal);
    }

    private static IReadOnlyList<UiSemanticNode> SemanticUiNodes(JsonElement ui)
    {
        var result = new List<UiSemanticNode>();
        foreach (var node in ui.GetProperty("nodes").EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object) continue;
            result.Add(new UiSemanticNode(
                OptionalString(node, "id") ?? "",
                OptionalString(node, "text") ?? "",
                OptionalString(node, "state") ?? "",
                OptionalBoolean(node, "visible"),
                OptionalBoolean(node, "enabled"),
                RequiredNormalized(node, "x"),
                RequiredNormalized(node, "y"),
                RequiredNormalized(node, "width"),
                RequiredNormalized(node, "height"),
                OptionalString(node, "subjectId"),
                OptionalString(node, "operation"),
                OptionalLong(node, "revision")));
        }
        return result;
    }

    private static double RequiredNormalized(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || !property.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number is < 0 or > 1)
            throw new InvalidDataException($"semantic-ui-node-{name}-invalid");
        return number;
    }

    private static string? ResolveInventorySubjectId(JsonElement state, string selector)
    {
        if (!state.TryGetProperty("nativeInventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object ||
            !inventory.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;
        var matches = InventoryCandidates(inventory).Where(item => InventoryItemMatches(item, selector)).ToArray();
        if (matches.Length == 1 && matches[0].TryGetProperty("itemId", out var direct) &&
            direct.ValueKind == JsonValueKind.String) return direct.GetString();
        if (inventory.TryGetProperty("semanticSelectors", out var selectors) && selectors.ValueKind == JsonValueKind.Object)
            foreach (var candidate in FlattenSelectorValues(selectors))
                if (candidate.ValueKind == JsonValueKind.Object &&
                    candidate.TryGetProperty("semanticId", out var semantic) &&
                    string.Equals(semantic.GetString(), selector, StringComparison.OrdinalIgnoreCase) &&
                    candidate.TryGetProperty("itemId", out var itemId) && itemId.ValueKind == JsonValueKind.String &&
                    (!candidate.TryGetProperty("status", out var status) ||
                     string.Equals(status.GetString(), "PRESENT", StringComparison.OrdinalIgnoreCase)))
                    return itemId.GetString();
        return null;
    }

    private static IEnumerable<JsonElement> FlattenSelectorValues(JsonElement selectors)
    {
        foreach (var property in selectors.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray()) yield return item;
            else yield return property.Value;
        }
    }

    private static bool TryOperationEvidence(JsonElement state, out JsonElement evidence)
    {
        if (state.TryGetProperty("semanticUi", out var ui) && ui.ValueKind == JsonValueKind.Object &&
            ui.TryGetProperty("operationEvidence", out evidence) && evidence.ValueKind == JsonValueKind.Object)
            return true;
        if (state.TryGetProperty("nativeInventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object &&
            inventory.TryGetProperty("operationEvidence", out evidence) && evidence.ValueKind == JsonValueKind.Object)
            return true;
        evidence = default;
        return false;
    }

    private async Task<bool> WaitForSemanticOperationNodeAsync(
        string subjectId,
        string operation,
        CancellationToken cancellationToken) =>
        await WaitForSemanticUiAsync(ui => SemanticUiNodes(ui).Any(node => node.Enabled &&
            string.Equals(node.SubjectId, subjectId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.Operation, operation, StringComparison.Ordinal)), cancellationToken)
            .ConfigureAwait(false);

    private async Task<bool> WaitForSemanticUiAsync(
        Func<JsonElement, bool> predicate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
            var state = observation.WorldSnapshot.GetProperty("state");
            if (TrySemanticUi(state, out var ui) && predicate(ui)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<JsonElement?> ObserverEvidenceAsync(CancellationToken cancellationToken)
    {
        var observation = await ObserveWorkerAsync(cancellationToken).ConfigureAwait(false);
        var state = observation.WorldSnapshot.GetProperty("state");
        return TryQaEvidence(state, out var evidence) && evidence.TryGetProperty("events", out var events) &&
               events.ValueKind == JsonValueKind.Array ? evidence.Clone() : null;
    }

    private static bool TryQaEvidence(JsonElement state, out JsonElement evidence) =>
        state.TryGetProperty("qaEvidence", out evidence) && evidence.ValueKind == JsonValueKind.Object;
    private static string? EventName(JsonElement item) => item.TryGetProperty("event", out var value)
        ? value.GetString() : item.TryGetProperty("type", out value) ? value.GetString() : null;
    internal static long? EventTimestamp(JsonElement item)
    {
        foreach (var name in new[] { "atEpochMilliseconds", "timestampEpochMilliseconds", "at", "occurredAt" })
            if (item.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
                if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var instant))
                    return instant.ToUnixTimeMilliseconds();
            }
        return null;
    }
    internal static bool EvidenceSupports(JsonElement evidence, string field, string value) =>
        evidence.TryGetProperty(field, out var supported) && supported.ValueKind == JsonValueKind.Array &&
        supported.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
            string.Equals(item.GetString(), value, StringComparison.Ordinal));
    internal static bool TryHashEvidence(JsonElement evidence, string subject, out JsonElement actual)
    {
        if (evidence.TryGetProperty("hashChains", out var chains) && chains.ValueKind == JsonValueKind.Object &&
            chains.TryGetProperty(subject, out actual)) return true;
        if (evidence.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array)
            foreach (var candidate in hashes.EnumerateArray())
                if (candidate.ValueKind == JsonValueKind.Object &&
                    candidate.TryGetProperty("subject", out var name) && name.ValueKind == JsonValueKind.String &&
                    string.Equals(name.GetString(), subject, StringComparison.Ordinal))
                { actual = candidate; return true; }
        actual = default;
        return false;
    }
    private static bool EventFieldMatches(JsonElement item, JsonProperty expected)
    {
        if (item.TryGetProperty(expected.Name, out var direct)) return JsonSubset(direct, expected.Value);
        return item.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
               attributes.TryGetProperty(expected.Name, out var nested) && JsonSubset(nested, expected.Value);
    }
    internal static bool InventoryItemMatches(JsonElement item, string selector)
    {
        foreach (var name in new[] { "itemId", "assetId", "idempotencyKey", "rewardId", "base", "semanticId", "selectorId" })
            if (item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                string.Equals(value.GetString(), selector, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    internal static IReadOnlyList<JsonElement> InventoryCandidates(JsonElement inventory)
    {
        var candidates = inventory.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Select(item => item.Clone()).ToList()
            : new List<JsonElement>();
        if (!inventory.TryGetProperty("semanticSelectors", out var selectors) ||
            selectors.ValueKind != JsonValueKind.Object) return candidates;
        candidates.AddRange(FlattenSelectorValues(selectors)
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                (!item.TryGetProperty("status", out var status) ||
                 string.Equals(status.GetString(), "PRESENT", StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Clone()));
        return candidates;
    }
    private static bool IsSemanticInventorySelector(string selector) =>
        selector.StartsWith("semantic.", StringComparison.OrdinalIgnoreCase) ||
        selector.StartsWith("fixture.", StringComparison.OrdinalIgnoreCase);
    private static bool TryInventoryExpectation(JsonElement item, string name, out JsonElement value)
    {
        if (item.TryGetProperty(name, out value)) return true;
        object? derived = name switch
        {
            "currentNativeLocation" => item.TryGetProperty("location", out var location) &&
                                       !string.IsNullOrWhiteSpace(location.GetString()),
            "provenanceValid" => OptionalBoolean(item, "signatureValid"),
            _ => null
        };
        if (derived is null) return false;
        value = JsonSerializer.SerializeToElement(derived);
        return true;
    }

    private async Task<QaStepResult> AssertVisualAsync(QaScenarioStep step, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(artifacts);
        var capture = await worker.ScreenshotAsync(NextScreenshot("visual"), cancellationToken).ConfigureAwait(false);
        if (step.Expect is null) return Untested("visual-expectation-empty", "Screenshot captured without an objective assertion.");
        var supported = step.Expect.Value.EnumerateObject().All(property => property.Name is "nonBlank" or "changedFromLaunch");
        if (!supported) return Untested("visual-classifier-unavailable",
            $"Screenshot {capture.Sha256} was captured, but locator/model/UI claims require a classifier that is not implemented.");
        var bytes = await File.ReadAllBytesAsync(capture.Path, cancellationToken).ConfigureAwait(false);
        var nonBlank = bytes.Skip(Math.Min(bytes.Length, 54)).Distinct().Take(3).Count() >= 3;
        if (step.Expect.Value.TryGetProperty("nonBlank", out var expectedBlank) && expectedBlank.GetBoolean() != nonBlank)
            return new(false, "visual-nonblank-mismatch", "Screenshot pixel diversity did not match nonBlank expectation.", true);
        if (step.Expect.Value.TryGetProperty("changedFromLaunch", out var changed) && changed.GetBoolean() ==
            string.Equals(capture.Sha256, launchScreenshotSha256, StringComparison.OrdinalIgnoreCase))
            return new(false, "visual-change-mismatch", "Screenshot change did not match changedFromLaunch expectation.", true);
        return Pass("visual-objective-assertion", $"Objective bitmap assertions passed for screenshot {capture.Sha256}.");
    }

    private string NextScreenshot(string label) =>
        $"{Sanitize(Path.GetFileName(artifacts))}-{Sanitize(scenario.Id)}-{++screenshotSequence:D4}-{label}.bmp";
    private static string Sanitize(string value) => string.Concat(value.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
    private static (string Kind, string? Id, double Within) Target(QaScenarioStep step)
    {
        if (step.Target is null || step.Target.Value.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("observer-target-missing");
        var target = step.Target.Value;
        var kind = target.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "" : "";
        var id = target.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        var within = target.TryGetProperty("within", out var distance) ? distance.GetDouble() : 2.5;
        return (kind, id, within);
    }

    private static string? TargetRole(QaScenarioStep step) => step.Target is { } target && target.TryGetProperty("role", out var role)
        ? role.GetString() : null;
    private static string? TargetState(QaScenarioStep step) => step.Target is { } target && target.TryGetProperty("state", out var state)
        ? state.GetString() : null;
    private static bool ObjectivePresent(JsonElement state, string? id) => state.TryGetProperty("rooms", out var rooms) &&
        rooms.EnumerateArray().SelectMany(room => room.GetProperty("objectives").EnumerateArray())
            .Any(objective => string.Equals(objective.GetProperty("objectiveId").GetString(), id, StringComparison.Ordinal));
    private static bool SameWorldBoundary(OfflineServerProof left, OfflineServerProof right) => left.Kind == right.Kind &&
        string.Equals(left.ServerEndpoint, right.ServerEndpoint, StringComparison.Ordinal) &&
        string.Equals(left.Launcher?.WorldId, right.Launcher?.WorldId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Launcher?.LaunchEvidenceSha256, right.Launcher?.LaunchEvidenceSha256, StringComparison.OrdinalIgnoreCase);
    internal static bool TryResolveAssertValue(JsonElement state, string name, out JsonElement value)
    {
        if (state.TryGetProperty(name, out value)) return true;
        if (!state.TryGetProperty("nativeInventory", out var inventory) || inventory.ValueKind != JsonValueKind.Object ||
            !inventory.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;
        object? derived = name switch
        {
            "signedItemCount" => inventory.TryGetProperty("signedStackCount", out var signed) && signed.TryGetInt32(out var count)
                ? count : items.EnumerateArray().Count(item => OptionalBoolean(item, "signatureValid")),
            "provenanceValid" => items.GetArrayLength() > 0 &&
                                 items.EnumerateArray().All(item => OptionalBoolean(item, "signatureValid")),
            "activeItemCount" => items.EnumerateArray().Count(item => OptionalBoolean(item, "active")),
            _ => null
        };
        if (derived is null) return false;
        value = JsonSerializer.SerializeToElement(derived);
        return true;
    }

    private static bool JsonSubset(JsonElement actual, JsonElement expected)
    {
        if (expected.ValueKind == JsonValueKind.Object)
        {
            if (actual.ValueKind != JsonValueKind.Object) return false;
            return expected.EnumerateObject().All(property => actual.TryGetProperty(property.Name, out var child) &&
                JsonSubset(child, property.Value));
        }
        if (expected.ValueKind == JsonValueKind.Array)
        {
            if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() != expected.GetArrayLength()) return false;
            return actual.EnumerateArray().Zip(expected.EnumerateArray()).All(pair => JsonSubset(pair.First, pair.Second));
        }
        if (expected.ValueKind == JsonValueKind.Number && actual.ValueKind == JsonValueKind.Number)
            return actual.GetDouble().Equals(expected.GetDouble());
        return actual.ValueKind == expected.ValueKind && actual.GetRawText() == expected.GetRawText();
    }
    private static bool OptionalBoolean(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True && value.GetBoolean();
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
    private static bool? ExpectedBoolean(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False ? property.GetBoolean() : null;
    private static string? ExpectedString(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static int? ExpectedInt(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) &&
        property.TryGetInt32(out var number) ? number : null;
    private static bool AuthoritativeInteractionChanged(JsonElement before, JsonElement after)
    {
        string Raw(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetRawText() : "<missing>";
        return new[] { "runId", "directorRevision", "currentRoomId", "currentRoomState", "currentObjectiveId",
                "objectiveProgress", "selectedRoute", "completed", "bossPhase", "rewardStatus", "extractionStatus" }
            .Any(name => !string.Equals(Raw(before, name), Raw(after, name), StringComparison.Ordinal));
    }
    private Task<ObserverObservation> ObserveWorkerAsync(CancellationToken cancellationToken) =>
        ObserveWithTransientRetryAsync(worker.ObserveSolePlayerAsync, cancellationToken);

    private async Task<ObserverObservation> ObserveWithTransientRetryAsync(
        Func<CancellationToken, Task<ObserverObservation>> observe,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return ValidateBoundObservation(
                    await observe(cancellationToken).ConfigureAwait(false));
            }
            catch (ObserverSpoolException failure) when (
                IsTransientObservationFailure(failure) && attempt < 39)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    internal static bool IsTransientObservationFailure(ObserverSpoolException failure) =>
        string.Equals(failure.Code, "observer-server-snapshot-unavailable",
            StringComparison.Ordinal);

    internal static bool LauncherWorldMatches(
        JsonElement worldSnapshot,
        string launcherWorldId)
    {
        if (!worldSnapshot.TryGetProperty("worldId", out var observedValue) ||
            observedValue.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(observedValue.GetString(), out var observedWorld) ||
            !Guid.TryParse(launcherWorldId, out var rootWorld))
            return false;
        if (observedWorld == rootWorld) return true;
        if (!worldSnapshot.TryGetProperty("state", out var state) ||
            state.ValueKind != JsonValueKind.Object ||
            !state.TryGetProperty("launcherWorldContext", out var context) ||
            context.ValueKind != JsonValueKind.Object)
            return false;
        if (!string.Equals(OptionalString(context, "schema"),
                "hytale-qa-launcher-world-context-v1", StringComparison.Ordinal) ||
            !string.Equals(OptionalString(context, "scope"),
                "owned_instance", StringComparison.Ordinal) ||
            !OptionalBoolean(context, "playerRunAttached") ||
            !OptionalBoolean(state, "runAttached"))
            return false;
        if (!Guid.TryParse(OptionalString(context, "rootWorldId"), out var contextRoot) ||
            !Guid.TryParse(OptionalString(context, "currentWorldId"), out var contextCurrent) ||
            !Guid.TryParse(OptionalString(context, "ownerRunId"), out var ownerRun) ||
            !Guid.TryParse(OptionalString(state, "runId"), out var activeRun))
            return false;
        if (contextRoot != rootWorld || contextCurrent != observedWorld || ownerRun != activeRun)
            return false;
        return string.Equals(OptionalString(context, "currentWorldName"),
            OptionalString(state, "worldName"), StringComparison.Ordinal);
    }

    private ObserverObservation ValidateBoundObservation(ObserverObservation observation)
    {
        PersistObservationEvidence(observation);
        if (proofBoundPlayerId is not { } expected) return observation;
        var snapshot = observation.WorldSnapshot;
        if (!snapshot.TryGetProperty("playerId", out var value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var actual) || actual != expected)
            throw new ObserverBindingDriftException("observer-proof-bound-player-changed",
                "The sole observed player no longer matches the identity bound before physical input attachment.",
                QaRunOutcome.InvalidEvidence);
        if (pinnedProof is { } proof)
        {
            var validation = ObserverFixtureBinding.Validate(proof.Kind, scenario.Fixture, snapshot, fixturePins);
            if (!validation.Valid)
                throw new ObserverBindingDriftException(validation.Code, validation.Message, validation.Outcome);
        }
        return observation;
    }

    private void PersistObservationEvidence(ObserverObservation observation)
    {
        if (observation.ObservationSequence <= lastPersistedObservationSequence) return;
        Directory.CreateDirectory(artifacts);
        var prefix = $"{Sanitize(Path.GetFileName(artifacts))}-{Sanitize(scenario.Id)}";
        var packetPath = Path.Combine(artifacts,
            $"{prefix}-packet-evidence-{observation.ObservationSequence:D12}.json");
        var snapshot = observation.WorldSnapshot.Clone();
        var packet = new
        {
            schema = "hytale-qa-packet-evidence-v1",
            observationSequence = observation.ObservationSequence,
            observation.ObservedAt,
            observation.EvidenceValid,
            packetBatch = observation.PacketBatch,
            rawSha256 = Sha256(observation.Raw.GetRawText()),
            worldSnapshotSha256 = Sha256(snapshot.GetRawText())
        };
        WriteAppendOnly(packetPath, packet);

        var previous = previousEvidenceSnapshot;
        var statePath = Path.Combine(artifacts,
            $"{prefix}-state-diff-{observation.ObservationSequence:D12}.json");
        var stateDiff = new
        {
            schema = "hytale-qa-state-diff-v1",
            observationSequence = observation.ObservationSequence,
            observation.ObservedAt,
            baseline = previous is null,
            previousSnapshotSha256 = previous is null ? null : Sha256(previous.Value.GetRawText()),
            currentSnapshotSha256 = Sha256(snapshot.GetRawText()),
            changedTopLevelFields = ChangedTopLevelFields(previous, snapshot),
            currentSnapshot = snapshot
        };
        WriteAppendOnly(statePath, stateDiff);
        previousEvidenceSnapshot = snapshot;
        lastPersistedObservationSequence = observation.ObservationSequence;
    }

    private static string[] ChangedTopLevelFields(JsonElement? previous, JsonElement current)
    {
        if (previous is null || previous.Value.ValueKind != JsonValueKind.Object || current.ValueKind != JsonValueKind.Object)
            return current.ValueKind == JsonValueKind.Object
                ? current.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray()
                : ["$"];
        var before = previous.Value.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.GetRawText(), StringComparer.Ordinal);
        var after = current.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.GetRawText(), StringComparer.Ordinal);
        return before.Keys.Union(after.Keys, StringComparer.Ordinal)
            .Where(key => !before.TryGetValue(key, out var left) || !after.TryGetValue(key, out var right) ||
                          !string.Equals(left, right, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private static void WriteAppendOnly<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { WriteIndented = true });
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Append-only observation evidence already exists with different content.");
        }
    }

    private static long OptionalLong(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : -1;
    private static QaStepResult Pass(string code, string message) => new(true, code, message);
    private static QaStepResult Blocked(string code, string message) => new(false, code, message, false, QaRunOutcome.Blocked);
    private static QaStepResult Untested(string code, string message) => new(false, code, message, false, QaRunOutcome.Untested);
    private sealed class ObserverBindingDriftException(string code, string message, QaRunOutcome outcome)
        : InvalidOperationException(message)
    {
        public string Code { get; } = code;
        public QaRunOutcome Outcome { get; } = outcome;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
