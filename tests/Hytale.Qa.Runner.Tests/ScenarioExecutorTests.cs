using System.Text.Json;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Runner.Tests;

public sealed class ScenarioExecutorTests
{
    [Fact]
    public async Task PassedRunPinsClientPerformsDeclaredStopReleasesInputsAndChainsEvidence()
    {
        var runtime = new FakeRuntime();

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.True(report.Passed);
        Assert.Equal(QaRunOutcome.Passed, report.Outcome);
        Assert.True(runtime.Released);
        Assert.Equal(1, runtime.Operations.Count(operation => operation == "session.stop"));
        Assert.True(report.Teardown.SessionStopSucceeded);
        Assert.True(report.Teardown.InputsReleased);
        Assert.Equal(64, report.ScenarioCanonicalSha256.Length);
        Assert.True(QaEvidenceChain.Verify(report.Evidence, out var root));
        Assert.Equal(root, report.EvidenceRootSha256);
        Assert.Contains(report.Evidence, item => item.Kind == "scenario.start" &&
            item.Payload.GetProperty("CanonicalSha256").GetString() == report.ScenarioCanonicalSha256);
        Assert.Contains(report.Evidence, item => item.Kind == "step.result");
        Assert.Contains(report.Evidence, item => item.Kind == "teardown.result");
        Assert.Equal("run.final", report.Evidence[^1].Kind);
    }

    [Fact]
    public async Task PidDriftAbortsForSafetyThenStopsBeforeReleasingInputs()
    {
        var runtime = new FakeRuntime { DriftPidAfterCalls = 3 };

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.Equal(QaRunOutcome.AbortedSafety, report.Outcome);
        Assert.Equal("client-identity-drift", report.Code);
        Assert.True(report.Teardown.SessionStopAttempted);
        Assert.True(report.Teardown.SessionStopSucceeded);
        Assert.True(runtime.Released);
        Assert.True(runtime.CallOrder.IndexOf("session.stop") < runtime.CallOrder.IndexOf("release-all"));
        Assert.Contains(report.Evidence, item => item.Kind == "safety.failure");
    }

    [Fact]
    public async Task UnknownModalStateAbortsInsteadOfAssumingNoModal()
    {
        var runtime = new FakeRuntime();
        runtime.SafetyFactory = _ => runtime.Snapshot(false) with { ModalVisible = null };

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.Equal(QaRunOutcome.AbortedSafety, report.Outcome);
        Assert.Equal("modal-state-unavailable", report.Code);
        Assert.True(report.Teardown.SessionStopAttempted);
    }

    [Fact]
    public async Task InvalidScenarioCapabilityReturnsTruthfulSafetyOutcomeWithoutStartingSession()
    {
        var scenario = Scenario() with
        {
            Capabilities = new HashSet<string>(["direct_damage"], StringComparer.Ordinal)
        };
        var runtime = new FakeRuntime();

        var report = await new ScenarioExecutor(runtime).RunAsync(scenario);

        Assert.Equal(QaRunOutcome.AbortedSafety, report.Outcome);
        Assert.Equal("forbidden-capability", report.Code);
        Assert.DoesNotContain("session.start", runtime.Operations);
        Assert.False(report.Teardown.SessionStopAttempted);
        Assert.True(report.Teardown.InputsReleased);
    }

    [Fact]
    public async Task FixtureAndFaultCapabilitiesAreAllowedOnlyInWhiteBoxMode()
    {
        var capabilities = new HashSet<string>(["physical_input", "state_setup", "fault_injection"], StringComparer.Ordinal);
        var whiteBox = Scenario() with { Mode = "white_box", Capabilities = capabilities };
        var guided = Scenario() with { Capabilities = capabilities };

        Assert.Equal(QaRunOutcome.Passed,
            (await new ScenarioExecutor(new FakeRuntime()).RunAsync(whiteBox)).Outcome);
        Assert.Equal(QaRunOutcome.AbortedSafety,
            (await new ScenarioExecutor(new FakeRuntime()).RunAsync(guided)).Outcome);
    }

    [Fact]
    public async Task FailedStepUsesBestEffortStopBeforeRelease()
    {
        var runtime = new FakeRuntime
        {
            StepResult = step => step.Operation == "trace.mark"
                ? new(false, "objective-failed", "Objective evidence did not match.")
                : new(true, "ok", "ok")
        };

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.Equal(QaRunOutcome.Failed, report.Outcome);
        Assert.Equal("objective-failed", report.Code);
        Assert.True(report.Teardown.SessionStopAttempted);
        Assert.True(report.Teardown.SessionStopSucceeded);
        Assert.True(runtime.CallOrder.IndexOf("session.stop") < runtime.CallOrder.IndexOf("release-all"));
        Assert.Contains(report.Evidence, item => item.Kind == "teardown.session-stop");
    }

    [Theory]
    [InlineData(QaRunOutcome.Blocked)]
    [InlineData(QaRunOutcome.Untested)]
    [InlineData(QaRunOutcome.InvalidEvidence)]
    [InlineData(QaRunOutcome.AbortedSafety)]
    public async Task RuntimeCanReportNonPassingOutcomeWithoutItBeingCollapsedToFailed(QaRunOutcome expected)
    {
        var runtime = new FakeRuntime
        {
            StepResult = step => step.Operation == "trace.mark"
                ? new(false, expected.ToString().ToLowerInvariant(), "truthful runtime result", false, expected)
                : new(true, "ok", "ok")
        };

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.Equal(expected, report.Outcome);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task NoProgressBudgetFailsEvenWhenOperationClaimsSuccess()
    {
        var clock = new ManualTimeProvider();
        var runtime = new FakeRuntime
        {
            StepResult = step =>
            {
                if (step.Operation == "trace.mark")
                {
                    clock.Advance(TimeSpan.FromSeconds(3));
                    return new(true, "observed", "No measurable objective progress.", false);
                }
                return new(true, "ok", "ok");
            }
        };

        var report = await new ScenarioExecutor(runtime, clock).RunAsync(Scenario(noProgressSeconds: 2));

        Assert.Equal(QaRunOutcome.Failed, report.Outcome);
        Assert.Equal("no-progress-timeout", report.Code);
        Assert.Contains(report.Steps, step => step.Result.Code == "no-progress-timeout");
    }

    [Fact]
    public async Task OneDeathIsAllowedButSecondDeathAbortsAtConfiguredBudget()
    {
        var runtime = new FakeRuntime();
        runtime.SafetyFactory = call => runtime.Snapshot(call is 1 or 2 or 4);

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario(maximumDeaths: 1));

        Assert.Equal(QaRunOutcome.AbortedSafety, report.Outcome);
        Assert.Equal("death-budget-exhausted", report.Code);
        Assert.Equal(2, report.Deaths);
        Assert.True(report.Teardown.SessionStopSucceeded);
    }

    [Fact]
    public async Task SingleDeathWithinBudgetDoesNotFailRun()
    {
        var runtime = new FakeRuntime();
        runtime.SafetyFactory = call => runtime.Snapshot(call is 1 or 2);

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario(maximumDeaths: 1));

        Assert.Equal(QaRunOutcome.Passed, report.Outcome);
        Assert.Equal(1, report.Deaths);
    }

    [Fact]
    public async Task ContradictoryStepOutcomeIsInvalidEvidence()
    {
        var runtime = new FakeRuntime
        {
            StepResult = step => step.Operation == "trace.mark"
                ? new(true, "impossible", "Pass cannot be blocked.", true, QaRunOutcome.Blocked)
                : new(true, "ok", "ok")
        };

        var report = await new ScenarioExecutor(runtime).RunAsync(Scenario());

        Assert.Equal(QaRunOutcome.InvalidEvidence, report.Outcome);
        Assert.Equal("step-outcome-contradiction", report.Code);
    }

    private static QaScenario Scenario(int maximumDeaths = 0, int noProgressSeconds = 2)
    {
        var empty = JsonSerializer.SerializeToElement(new { });
        return new(
            "hytale-qa/v1",
            "test.safe",
            "Runner test scenario",
            ["runner", "test"],
            "guided_physical",
            new HashSet<string>(["physical_input"], StringComparer.Ordinal),
            new("hytale-qa-offline", "runner-fixture-v1", 11, 22,
                new(Guid.Parse("00000000-0000-0000-0000-000000000111"), "HYTALE_QA_Runner"),
                new(1600, 900, "borderless", 1, 80, "hytale-qa-default-v1", "hytale-qa-stable-v1"),
                "runner-loadout-v1"),
            new(true, true, 1, true, true, new HashSet<string>(StringComparer.Ordinal)),
            new(30, 5, noProgressSeconds, maximumDeaths, 0),
            [
                new("start", "session.start", null, null, empty, 5, null, null),
                new("act", "trace.mark", null, null, empty, 5, null, null),
                new("stop", "session.stop", null, null, empty, 5, null, null)
            ],
            empty,
            new string('a', 64));
    }

    private sealed class FakeRuntime : IQaScenarioRuntime
    {
        private int safetyCalls;
        private bool stopped;
        public int DriftPidAfterCalls { get; init; } = int.MaxValue;
        public bool Released { get; private set; }
        public Func<QaScenarioStep, QaStepResult> StepResult { get; init; } = _ => new(true, "ok", "ok");
        public Func<int, QaSafetySnapshot>? SafetyFactory { get; set; }
        public List<string> Operations { get; } = [];
        public List<string> CallOrder { get; } = [];

        public Task<QaSafetySnapshot> SafetyAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            safetyCalls++;
            return Task.FromResult(SafetyFactory?.Invoke(safetyCalls) ?? Snapshot(false));
        }

        public QaSafetySnapshot Snapshot(bool dead)
        {
            var pid = safetyCalls >= DriftPidAfterCalls ? 202 : 101;
            return new(true, true, true, true, true, stopped ? 0 : 1,
                stopped ? null : pid, stopped ? null : DateTimeOffset.UnixEpoch,
                true, true, false, dead, 0);
        }

        public Task<QaStepResult> ExecuteAsync(QaScenarioStep step, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Operations.Add(step.Operation);
            CallOrder.Add(step.Operation);
            var result = StepResult(step);
            if (step.Operation == "session.stop" && result.Passed) stopped = true;
            return Task.FromResult(result);
        }

        public Task ReleaseAllInputsAsync(CancellationToken cancellationToken)
        {
            Released = true;
            CallOrder.Add("release-all");
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            ticks += duration.Ticks;
            utcNow += duration;
        }
    }
}
