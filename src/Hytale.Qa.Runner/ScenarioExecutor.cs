namespace Hytale.Qa.Runner;

public sealed record QaSafetySnapshot(
    bool OfflineProven,
    bool LoopbackOnly,
    bool ServerArmed,
    bool ObserverFresh,
    bool PacketEvidenceValid,
    int ClientProcessCount,
    int? ClientPid,
    DateTimeOffset? ClientStartedAt,
    bool ClientForeground,
    bool EndpointUnchanged,
    bool? ModalVisible,
    bool PlayerDead,
    int HeldInputCount);

public enum QaRunOutcome
{
    Passed,
    Failed,
    Blocked,
    Untested,
    InvalidEvidence,
    AbortedSafety
}

public sealed record QaStepResult(
    bool Passed,
    string Code,
    string Message,
    bool Progress = true,
    QaRunOutcome? Outcome = null);

public sealed record QaStepReport(
    string StepId,
    string Operation,
    int Attempts,
    TimeSpan Duration,
    QaStepResult Result);

public sealed record QaTeardownReport(
    bool SessionStopAttempted,
    bool SessionStopSucceeded,
    bool InputsReleased,
    string Code,
    string Message,
    TimeSpan Duration);

public sealed record QaRunReport(
    string ScenarioId,
    QaRunOutcome Outcome,
    string Code,
    IReadOnlyList<QaStepReport> Steps,
    int Recoveries,
    int Deaths,
    QaTeardownReport Teardown,
    string ScenarioCanonicalSha256,
    IReadOnlyList<QaEvidenceItem> Evidence,
    string EvidenceRootSha256,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt)
{
    public bool Passed => Outcome == QaRunOutcome.Passed;
}

public interface IQaScenarioRuntime
{
    Task<QaSafetySnapshot> SafetyAsync(CancellationToken cancellationToken);
    Task<QaStepResult> ExecuteAsync(QaScenarioStep step, CancellationToken cancellationToken);
    Task ReleaseAllInputsAsync(CancellationToken cancellationToken);
}

public sealed class ScenarioExecutor
{
    private static readonly HashSet<string> NeverPermitted = ["teleport", "direct_damage", "packet.inject", "command.execute", "input.raw"];
    private static readonly HashSet<string> WhiteBoxOnly = ["state_setup", "fault_injection"];
    private readonly IQaScenarioRuntime runtime;
    private readonly TimeProvider time;
    private (int Pid, DateTimeOffset StartedAt)? pinnedClient;
    private bool playerWasDead;
    private int observedDeaths;

    public ScenarioExecutor(IQaScenarioRuntime runtime, TimeProvider? time = null)
    {
        this.runtime = runtime;
        this.time = time ?? TimeProvider.System;
    }

    public async Task<QaRunReport> RunAsync(QaScenario scenario, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        pinnedClient = null;
        playerWasDead = false;
        observedDeaths = 0;
        var startedAt = time.GetUtcNow();
        var reports = new List<QaStepReport>();
        var evidence = new QaEvidenceChain();
        var recoveries = 0;
        var outcome = QaRunOutcome.Passed;
        var code = "passed";
        var sessionStartInvoked = false;
        var sessionStopSucceeded = false;
        var teardown = new QaTeardownReport(false, false, false, "not-required", "Scenario did not start.", TimeSpan.Zero);
        var lastProgress = time.GetTimestamp();
        var stopStep = scenario.Steps.LastOrDefault(step => step.Operation == "session.stop")
            ?? new QaScenarioStep("teardown-stop", "session.stop", null, null, null, 30, null, null);

        evidence.Append("scenario.start", scenario.Id, new
        {
            scenario.Schema,
            scenario.Id,
            scenario.Mode,
            scenario.CanonicalSha256,
            scenario.Fixture.ServerProfile,
            scenario.Fixture.WorldSeed,
            scenario.Fixture.RunSeed,
            playerId = scenario.Fixture.Player.Uuid,
            scenario.Fixture.Player.Name
        }, startedAt);

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(TimeSpan.FromSeconds(scenario.Budgets.TotalSeconds));
        try
        {
            ValidateScenarioSafety(scenario);
            foreach (var step in scenario.Steps)
            {
                if (step.Operation is not ("session.start" or "client.launch_offline"))
                    await AssertSafetyAsync(scenario, step.Id, step.Operation == "session.stop", evidence, total.Token)
                        .ConfigureAwait(false);

                var began = time.GetTimestamp();
                var attempts = 0;
                QaStepResult result;
                var maximumAttempts = 1 + Math.Min(step.Recovery?.MaximumAttempts ?? 0, 5);
                if (step.Operation == "session.start") sessionStartInvoked = true;
                do
                {
                    attempts++;
                    using var stepTimeout = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                    stepTimeout.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds ?? scenario.Budgets.StepSeconds));
                    result = Normalize(await runtime.ExecuteAsync(step, stepTimeout.Token).ConfigureAwait(false));

                    if (result.Progress) lastProgress = time.GetTimestamp();
                    else if ((result.Outcome is null or QaRunOutcome.Failed) &&
                             time.GetElapsedTime(lastProgress) >= TimeSpan.FromSeconds(scenario.Budgets.NoProgressSeconds))
                        result = new(false, "no-progress-timeout",
                            $"No objective progress was observed for {scenario.Budgets.NoProgressSeconds} seconds.",
                            false, QaRunOutcome.Failed);

                    evidence.Append("step.attempt", step.Id, new
                    {
                        step.Operation,
                        attempt = attempts,
                        maximumAttempts,
                        result.Passed,
                        result.Code,
                        result.Message,
                        result.Progress,
                        outcome = result.Outcome?.ToString()
                    }, time.GetUtcNow());

                    if (result.Passed || IsTerminal(result) || attempts >= maximumAttempts) break;
                    recoveries++;
                    if (recoveries > scenario.Budgets.MaximumRecoveries)
                    {
                        result = new(false, "recovery-budget-exhausted",
                            "Scenario recovery budget was exceeded.", false, QaRunOutcome.Failed);
                        break;
                    }
                    await AssertSafetyAsync(scenario, step.Id, false, evidence, total.Token).ConfigureAwait(false);
                } while (true);

                var report = new QaStepReport(step.Id, step.Operation, attempts, time.GetElapsedTime(began), result);
                reports.Add(report);
                evidence.Append("step.result", step.Id, report, time.GetUtcNow());
                if (!result.Passed)
                {
                    outcome = FailureOutcome(result);
                    code = result.Code;
                    break;
                }
                if (step.Operation == "session.stop") sessionStopSucceeded = true;
                else if (step.Operation != "session.start")
                    await AssertSafetyAsync(scenario, step.Id, false, evidence, total.Token).ConfigureAwait(false);
            }

            if (outcome == QaRunOutcome.Passed)
            {
                var final = await runtime.SafetyAsync(total.Token).ConfigureAwait(false);
                evidence.Append("safety.final", scenario.Id, final, time.GetUtcNow());
                if (final.PlayerDead && !playerWasDead) observedDeaths++;
                playerWasDead = final.PlayerDead;
                if (observedDeaths > scenario.Budgets.MaximumDeaths)
                {
                    outcome = QaRunOutcome.AbortedSafety;
                    code = "death-budget-exhausted";
                }
                else if (final.HeldInputCount != 0 || final.ClientProcessCount > scenario.Safety.MaximumClientProcesses ||
                    (final.ClientProcessCount == 1 &&
                     (final.ClientPid != pinnedClient?.Pid || final.ClientStartedAt != pinnedClient?.StartedAt)))
                {
                    outcome = QaRunOutcome.Failed;
                    code = "cleanup-incomplete";
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = QaRunOutcome.Blocked;
            code = "scenario-cancelled";
            evidence.Append("run.blocked", scenario.Id, new { code }, time.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            outcome = QaRunOutcome.Failed;
            code = "scenario-timeout";
            evidence.Append("run.timeout", scenario.Id, new { code }, time.GetUtcNow());
        }
        catch (QaSafetyException failure)
        {
            outcome = QaRunOutcome.AbortedSafety;
            code = failure.Code;
            evidence.Append("safety.failure", scenario.Id,
                new { failure.Code, failure.Message }, time.GetUtcNow());
        }
        catch (Exception failure)
        {
            outcome = QaRunOutcome.Failed;
            code = "runtime-exception";
            evidence.Append("runtime.failure", scenario.Id,
                new { code, type = failure.GetType().FullName, failure.Message }, time.GetUtcNow());
        }
        finally
        {
            var teardownStarted = time.GetTimestamp();
            var stopAttempted = false;
            var stopSucceeded = sessionStopSucceeded;
            var inputsReleased = false;
            var teardownCode = sessionStopSucceeded ? "stop-step-passed" : "not-started";
            var teardownMessage = sessionStopSucceeded
                ? "The declared session.stop step completed."
                : "Scenario did not invoke session.start.";

            if (sessionStartInvoked && !sessionStopSucceeded)
            {
                stopAttempted = true;
                try
                {
                    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(
                        Math.Clamp(stopStep.TimeoutSeconds ?? 30, 1, 30)));
                    var stopResult = Normalize(await runtime.ExecuteAsync(stopStep, stopTimeout.Token).ConfigureAwait(false));
                    stopSucceeded = stopResult.Passed;
                    teardownCode = stopResult.Code;
                    teardownMessage = stopResult.Message;
                    evidence.Append("teardown.session-stop", stopStep.Id, stopResult, time.GetUtcNow());
                    if (!stopSucceeded && outcome == QaRunOutcome.Passed)
                    {
                        outcome = FailureOutcome(stopResult);
                        code = stopResult.Code;
                    }
                }
                catch (Exception failure)
                {
                    teardownCode = "teardown-stop-failed";
                    teardownMessage = failure.Message;
                    evidence.Append("teardown.session-stop", stopStep.Id,
                        new { passed = false, code = teardownCode, failure.Message }, time.GetUtcNow());
                    if (outcome == QaRunOutcome.Passed)
                    {
                        outcome = QaRunOutcome.Failed;
                        code = teardownCode;
                    }
                }
            }

            try
            {
                await runtime.ReleaseAllInputsAsync(CancellationToken.None).ConfigureAwait(false);
                inputsReleased = true;
                evidence.Append("teardown.release-inputs", scenario.Id,
                    new { released = true }, time.GetUtcNow());
            }
            catch (Exception failure)
            {
                evidence.Append("teardown.release-inputs", scenario.Id,
                    new { released = false, failure.Message }, time.GetUtcNow());
                if (outcome == QaRunOutcome.Passed)
                {
                    outcome = QaRunOutcome.AbortedSafety;
                    code = "input-release-failed";
                }
                teardownCode = "input-release-failed";
                teardownMessage = failure.Message;
            }

            teardown = new(
                sessionStopSucceeded || stopAttempted,
                stopSucceeded,
                inputsReleased,
                teardownCode,
                teardownMessage,
                time.GetElapsedTime(teardownStarted));
            evidence.Append("teardown.result", scenario.Id, teardown, time.GetUtcNow());
        }

        if (!evidence.Verify())
        {
            outcome = QaRunOutcome.InvalidEvidence;
            code = "evidence-chain-invalid";
        }
        var priorRoot = evidence.RootSha256;
        evidence.Append("run.final", scenario.Id, new
        {
            outcome = outcome.ToString(),
            code,
            scenario.CanonicalSha256,
            priorRootSha256 = priorRoot,
            stepCount = reports.Count,
            recoveries,
            deaths = observedDeaths,
            teardown
        }, time.GetUtcNow());
        if (!evidence.Verify())
        {
            outcome = QaRunOutcome.InvalidEvidence;
            code = "evidence-chain-invalid";
        }
        return new(
            scenario.Id,
            outcome,
            code,
            reports.ToArray(),
            recoveries,
            observedDeaths,
            teardown,
            scenario.CanonicalSha256,
            evidence.Items.ToArray(),
            evidence.RootSha256,
            startedAt,
            time.GetUtcNow());
    }

    private static QaStepResult Normalize(QaStepResult result)
    {
        if (result.Passed && result.Outcome is not null and not QaRunOutcome.Passed)
            return new(false, "step-outcome-contradiction",
                "A passing step reported a non-passing outcome.", false, QaRunOutcome.InvalidEvidence);
        if (!result.Passed && result.Outcome == QaRunOutcome.Passed)
            return new(false, "step-outcome-contradiction",
                "A failed step reported a passing outcome.", false, QaRunOutcome.InvalidEvidence);
        return result;
    }

    private static bool IsTerminal(QaStepResult result) =>
        result.Outcome is QaRunOutcome.Blocked or QaRunOutcome.Untested or
            QaRunOutcome.InvalidEvidence or QaRunOutcome.AbortedSafety;

    private static QaRunOutcome FailureOutcome(QaStepResult result) => result.Outcome switch
    {
        null => QaRunOutcome.Failed,
        QaRunOutcome.Passed => QaRunOutcome.InvalidEvidence,
        QaRunOutcome.Failed => QaRunOutcome.Failed,
        QaRunOutcome.Blocked => QaRunOutcome.Blocked,
        QaRunOutcome.Untested => QaRunOutcome.Untested,
        QaRunOutcome.InvalidEvidence => QaRunOutcome.InvalidEvidence,
        QaRunOutcome.AbortedSafety => QaRunOutcome.AbortedSafety,
        _ => QaRunOutcome.InvalidEvidence
    };

    private static void ValidateScenarioSafety(QaScenario scenario)
    {
        if (!scenario.Safety.OfflineRequired || !scenario.Safety.LoopbackOnly || scenario.Safety.MaximumClientProcesses != 1)
            throw new QaSafetyException("unsafe-scenario-contract");
        if (scenario.Capabilities.Overlaps(NeverPermitted) || scenario.Safety.ForbiddenCapabilities.Any(capability => !NeverPermitted.Contains(capability) && capability.Contains("packet", StringComparison.OrdinalIgnoreCase)))
            throw new QaSafetyException("forbidden-capability");
        if (scenario.Mode != "white_box" && scenario.Capabilities.Overlaps(WhiteBoxOnly))
            throw new QaSafetyException("mutating-assistance-forbidden");
    }

    private async Task AssertSafetyAsync(
        QaScenario scenario,
        string stepId,
        bool stopping,
        QaEvidenceChain evidence,
        CancellationToken cancellationToken)
    {
        QaSafetySnapshot state;
        try
        {
            state = await runtime.SafetyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure)
        {
            evidence.Append("safety.check", stepId,
                new { safe = false, code = "safety-snapshot-unavailable", failure.Message }, time.GetUtcNow());
            throw new QaSafetyException("safety-snapshot-unavailable", failure.Message, failure);
        }

        if (state.PlayerDead && !playerWasDead) observedDeaths++;
        playerWasDead = state.PlayerDead;
        string? violation = null;
        if (!stopping && (!state.OfflineProven || !state.LoopbackOnly || !state.ServerArmed)) violation = "offline-proof-lost";
        else if (!stopping && (!state.ObserverFresh || !state.PacketEvidenceValid)) violation = "observer-proof-lost";
        else if (state.ClientProcessCount is < 0 or > 1) violation = "client-count-drift";
        else if (state.ClientProcessCount == 1)
        {
            if (state.ClientPid is null || state.ClientStartedAt is null) violation = "client-identity-incomplete";
            else
            {
                var identity = (state.ClientPid.Value, state.ClientStartedAt.Value);
                if (pinnedClient is null) pinnedClient = identity;
                else if (pinnedClient != identity) violation = "client-identity-drift";
                else if (scenario.Safety.AbortOnFocusLoss && !state.ClientForeground) violation = "client-focus-lost";
            }
        }
        if (violation is null && scenario.Safety.AbortOnEndpointDrift && !state.EndpointUnchanged) violation = "endpoint-drift";
        if (violation is null && state.ModalVisible is null) violation = "modal-state-unavailable";
        else if (violation is null && state.ModalVisible == true) violation = "unexpected-modal";
        if (violation is null && observedDeaths > scenario.Budgets.MaximumDeaths) violation = "death-budget-exhausted";

        evidence.Append("safety.check", stepId, new
        {
            safe = violation is null,
            code = violation ?? "safe",
            stopping,
            state.OfflineProven,
            state.LoopbackOnly,
            state.ServerArmed,
            state.ObserverFresh,
            state.PacketEvidenceValid,
            state.ClientProcessCount,
            state.ClientPid,
            state.ClientStartedAt,
            state.ClientForeground,
            state.EndpointUnchanged,
            state.ModalVisible,
            state.PlayerDead,
            observedDeaths,
            state.HeldInputCount
        }, time.GetUtcNow());
        if (violation is not null) throw new QaSafetyException(violation);
    }
}

public sealed class QaSafetyException : Exception
{
    public QaSafetyException(string code, string? message = null, Exception? innerException = null)
        : base(message ?? code, innerException) => Code = code;

    public string Code { get; }
}
