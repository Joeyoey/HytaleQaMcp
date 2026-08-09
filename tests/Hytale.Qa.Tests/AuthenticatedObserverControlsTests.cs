using System.Numerics;
using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Tests;

public sealed class AuthenticatedObserverControlsTests
{
    [Fact]
    public void ResolvesExactSemanticTargetsCurrentRoomAndActiveGate()
    {
        var worldId = Guid.NewGuid().ToString("D");
        var openGate = Guid.NewGuid().ToString("D");
        var state = ObservedState.Parse(Observation(1, DateTimeOffset.UtcNow, worldId, new
        {
            positionX = 1, positionY = 2, positionZ = 3, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            currentRoomId = "room_alpha", currentObjectiveId = "objective.activate",
            objectiveWorldX = 10, objectiveWorldY = 20, objectiveWorldZ = 30,
            fallbackWorldX = 11, fallbackWorldY = 21, fallbackWorldZ = 31,
            semanticTargets = new[]
            {
                new { id = "left_route", role = "ROUTE_CHOICE", roomId = "choice_room", roomIndex = 4,
                    positionX = 40, positionY = 50, positionZ = 60 }
            },
            gates = new object[]
            {
                new { gateId = Guid.NewGuid(), state = "RETIRED", centerX = 70, centerY = 80, centerZ = 90 },
                new { gateId = openGate, state = "OPEN", centerX = 71, centerY = 81, centerZ = 91 }
            },
            encounters = Array.Empty<object>()
        }));

        Assert.Equal(new Vector3(40, 50, 60), state.ResolveTarget("objective", "anchor.left_route").Position);
        Assert.Equal(new Vector3(40, 50, 60), state.ResolveTarget("objective", "left_route").Position);
        Assert.Equal(new Vector3(40, 50, 60), state.ResolveTarget("anchor", "left_route").Position);
        Assert.Equal(new Vector3(40, 50, 60), state.ResolveTarget("block", "anchor.left_route").Position);
        Assert.Equal(new Vector3(10, 20, 30), state.ResolveTarget("objective", "room.room_alpha").Position);
        Assert.Equal(new Vector3(11, 21, 31), state.ResolveTarget("fallback", null).Position);
        Assert.Equal(new Vector3(71, 81, 91), state.ResolveTarget("gate", "active").Position);
        Assert.Equal(new Vector3(71, 81, 91), state.ResolveTarget("gate", openGate, "open").Position);
        Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("gate", "symbolic-gate"));
        Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("objective", "room.room_beta"));
    }

    [Fact]
    public void SemanticTargetsFailClosedWhenMissingOrDuplicated()
    {
        var worldId = Guid.NewGuid().ToString("D");
        object Target(string roomId, int roomIndex, int x) => new
        {
            id = "shared_switch", role = "OBJECTIVE", roomId, roomIndex,
            positionX = x, positionY = 5, positionZ = 6
        };
        var state = ObservedState.Parse(Observation(1, DateTimeOffset.UtcNow, worldId, new
        {
            positionX = 1, positionY = 2, positionZ = 3, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            semanticTargets = new[] { Target("room_one", 1, 10), Target("room_two", 2, 20) },
            gates = Array.Empty<object>(), encounters = Array.Empty<object>()
        }));

        Assert.Equal("observer-semantic-target-not-unique",
            Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("anchor", "shared_switch")).Message);
        Assert.Equal("observer-semantic-target-unavailable",
            Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("block", "missing_switch")).Message);
        Assert.Equal("observer-semantic-target-unavailable",
            Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("objective", "anchor.missing_switch")).Message);
    }

    [Fact]
    public void AttackTargetRejectsFriendlyPlayerNonHostileAndOffRunEntities()
    {
        var worldId = Guid.NewGuid().ToString("D");
        object Encounter(long id, int x, bool hostile, bool currentRun, bool isPlayer, bool friendlyNpc) => new
        {
            stableEntityId = id, positionX = x, positionY = 5, positionZ = 6, healthFraction = 1.0,
            alive = true, hostile, currentRun, isPlayer, friendlyNpc, crosshairTarget = false,
            role = "training_target", runId = currentRun ? "run" : "other", roomIndex = 1,
            objectiveId = "objective", evidenceId = $"evidence-{id}"
        };
        var state = ObservedState.Parse(Observation(1, DateTimeOffset.UtcNow, worldId, new
        {
            positionX = 0, positionY = 0, positionZ = 0, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            semanticTargets = Array.Empty<object>(), gates = Array.Empty<object>(),
            encounters = new[]
            {
                Encounter(1, 1, true, true, true, false),
                Encounter(2, 2, true, true, false, true),
                Encounter(3, 3, false, true, false, false),
                Encounter(4, 4, true, false, false, false),
                Encounter(5, 5, true, true, false, false)
            }
        }));

        Assert.Equal(new Vector3(5, 5, 6), state.ResolveAttackTarget("TrainingTarget").Position);
        Assert.Equal("observer-entity-role-not-found",
            Assert.Throws<InvalidOperationException>(() => state.ResolveAttackTarget("missing_target")).Message);
    }

    [Fact]
    public void ActiveGateIgnoresEveryNonActionableLifecycleStateAndFailsClosedOnAmbiguity()
    {
        var worldId = Guid.NewGuid().ToString("D");
        object Gate(string state, int x) => new
        {
            gateId = Guid.NewGuid(), semanticId = "qa-gateway", regionId = "qa-gateway-region",
            state, centerX = x, centerY = 80, centerZ = 90
        };
        var baseState = new
        {
            positionX = 1, positionY = 2, positionZ = 3, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            semanticTargets = Array.Empty<object>(), encounters = Array.Empty<object>()
        };
        var unique = ObservedState.Parse(Observation(1, DateTimeOffset.UtcNow, worldId, new
        {
            baseState.positionX, baseState.positionY, baseState.positionZ, baseState.headYaw, baseState.headPitch,
            baseState.grounded, baseState.jumping, baseState.falling,
            baseState.semanticTargets, baseState.encounters,
            gates = new[] { Gate("MANIFESTING", 70), Gate("IGNORED", 71), Gate("BREACHED", 72),
                Gate("CLEARED", 73), Gate("AFTERMATH", 74), Gate("RETIRED", 75), Gate("OPEN", 76) }
        }));

        Assert.Equal(new Vector3(76, 80, 90), unique.ResolveTarget("gate", "active").Position);
        Assert.Equal(new Vector3(72, 80, 90), unique.ResolveTarget("gate", "qa-gateway", "breached").Position);
        Assert.Equal("observer-gate-target-not-unique",
            Assert.Throws<InvalidOperationException>(() => unique.ResolveTarget("gate", "qa-gateway")).Message);

        var ambiguous = ObservedState.Parse(Observation(2, DateTimeOffset.UtcNow, worldId, new
        {
            baseState.positionX, baseState.positionY, baseState.positionZ, baseState.headYaw, baseState.headPitch,
            baseState.grounded, baseState.jumping, baseState.falling,
            baseState.semanticTargets, baseState.encounters,
            gates = new[] { Gate("OPEN", 80), Gate("ACTIVE", 81) }
        }));
        Assert.Equal("observer-gate-target-not-unique",
            Assert.Throws<InvalidOperationException>(() => ambiguous.ResolveTarget("gate", "active")).Message);
    }

    [Fact]
    public async Task NavigationInfersBlockageOnlyAfterGroundedForwardPulsesAndResetsOnWorldChange()
    {
        var now = DateTimeOffset.UtcNow;
        var firstWorld = Guid.NewGuid().ToString("D");
        var worker = new NavigationWorker([
            NavigationObservation(1, now, firstWorld),
            NavigationObservation(2, now.AddSeconds(1), firstWorld),
            NavigationObservation(3, now.AddSeconds(2), firstWorld),
            NavigationObservation(4, now.AddSeconds(3), Guid.NewGuid().ToString("D"))
        ]);
        var controls = new AuthenticatedObserverControls(worker);

        _ = await controls.NavigateAsync("objective", null, 0.5, CancellationToken.None);
        _ = await controls.NavigateAsync("objective", null, 0.5, CancellationToken.None);
        _ = await controls.NavigateAsync("objective", null, 0.5, CancellationToken.None);
        _ = await controls.NavigateAsync("objective", null, 0.5, CancellationToken.None);

        Assert.False(worker.NavigationStates[0].LineOfTravelBlocked);
        Assert.False(worker.NavigationStates[1].LineOfTravelBlocked);
        Assert.True(worker.NavigationStates[2].LineOfTravelBlocked);
        Assert.True(worker.NavigationStates[2].NoProgress >= TimeSpan.FromSeconds(2));
        Assert.False(worker.NavigationStates[3].LineOfTravelBlocked);
        Assert.Equal(TimeSpan.Zero, worker.NavigationStates[3].NoProgress);
    }

    [Fact]
    public void GateNavigationChoosesNearestAuthenticatedApproachButInteractionKeepsCore()
    {
        var now = DateTimeOffset.UtcNow;
        var gateId = Guid.NewGuid();
        var observation = Observation(1, now, Guid.NewGuid().ToString("D"), new
        {
            positionX = 8, positionY = 4, positionZ = 1, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            currentRoomId = "", currentObjectiveId = "",
            semanticTargets = Array.Empty<object>(), encounters = Array.Empty<object>(),
            gates = new[] { new
            {
                gateId, state = "OPEN", centerX = 0, centerY = 4, centerZ = 0,
                approaches = new[] { new { x = 0, y = 4, z = -4 }, new { x = 0, y = 4, z = 4 } }
            }}
        });
        var state = ObservedState.Parse(observation);

        var navigation = state.ResolveNavigationTarget("gate", gateId.ToString(), "open");
        var interaction = state.ResolveTarget("gate", gateId.ToString(), "open");

        Assert.Equal(new Vector3(0, 4, 4), navigation.Position);
        Assert.EndsWith(":approach:1", navigation.Key, StringComparison.Ordinal);
        Assert.Equal(new Vector3(0, 4, 0), interaction.Position);
    }

    [Fact]
    public async Task InteractionConvergesAuthenticatedAimBeforeClicking()
    {
        var now = DateTimeOffset.UtcNow;
        var worldId = Guid.NewGuid().ToString("D");
        var gateId = Guid.NewGuid();
        ObserverObservation GateObservation(long sequence, double yawRadians) =>
            Observation(sequence, now.AddMilliseconds(sequence * 100), worldId, new
            {
                positionX = 0, positionY = 0, positionZ = 0,
                headYaw = yawRadians, headPitch = 0,
                grounded = true, jumping = false, falling = false,
                currentRoomId = "", currentObjectiveId = "",
                semanticTargets = Array.Empty<object>(), encounters = Array.Empty<object>(),
                gates = new[] { new
                {
                    gateId, state = "OPEN", centerX = 0, centerY = 1.6, centerZ = -10
                }}
            });
        var worker = new NavigationWorker([
            GateObservation(1, Math.PI / 3),
            GateObservation(2, Math.PI / 9),
            GateObservation(3, Math.PI / 90)
        ]);
        var controls = new AuthenticatedObserverControls(worker);

        var result = await controls.InteractAsync(
            "gate", gateId.ToString("D"), "open", CancellationToken.None);

        Assert.True(result.Executed);
        Assert.Equal(2, worker.AimStates.Count);
        Assert.Equal(["left_click"], worker.Interactions);
        Assert.Contains("Aligned in 2", result.Message, StringComparison.Ordinal);
    }

    private static ObserverObservation NavigationObservation(long sequence, DateTimeOffset observedAt, string worldId) =>
        Observation(sequence, observedAt, worldId, new
        {
            positionX = 0, positionY = 4, positionZ = 0, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            currentRoomId = "room_alpha", currentObjectiveId = "objective.exit",
            objectiveWorldX = 0, objectiveWorldY = 9, objectiveWorldZ = -20,
            fallbackWorldX = 0, fallbackWorldY = 4, fallbackWorldZ = 0,
            semanticTargets = Array.Empty<object>(), gates = Array.Empty<object>(), encounters = Array.Empty<object>()
        });

    private static ObserverObservation Observation(long sequence, DateTimeOffset observedAt, string worldId, object state)
    {
        var world = JsonSerializer.SerializeToElement(new { schemaVersion = 1, worldId, state });
        return new(sequence, observedAt, true, JsonSerializer.SerializeToElement(new
        { schemaVersion = 1, valid = true, droppedFacts = 0 }), world, JsonSerializer.SerializeToElement(new { }));
    }

    private sealed class NavigationWorker(Queue<ObserverObservation> observations) : IWorkerControlService
    {
        public NavigationWorker(IEnumerable<ObserverObservation> observations) : this(new Queue<ObserverObservation>(observations)) { }
        public List<NavigationState> NavigationStates { get; } = [];
        public List<AimState> AimStates { get; } = [];
        public List<string> Interactions { get; } = [];
        public WorkerControlState State { get; } = new(WorkerControlPhase.Armed, 1, "lease", OfflineProofBoundaryKind.LauncherOwnedSingleplayer, DateTimeOffset.UtcNow, null, "test");
        public Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken) => Task.FromResult(observations.Dequeue());
        public Task<SemanticControlResult> NavigateStepAsync(NavigationState state, CancellationToken cancellationToken)
        {
            NavigationStates.Add(state);
            return Task.FromResult(new SemanticControlResult("navigate", DeterministicNavigator.Decide(state), SafetyCheck.Pass(), DateTimeOffset.UtcNow));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<WorkerControlState> AttachAsync(int processId, string sessionId, AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkerControlState> AttachLauncherAsync(int processId, string evidenceFileName, AssistanceMode assistanceMode, EvidenceCapabilities capabilities, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> InteractAsync(string semanticInput, CancellationToken cancellationToken)
        {
            Interactions.Add(semanticInput);
            return Task.FromResult(new SemanticControlResult("interact", [], SafetyCheck.Pass(), DateTimeOffset.UtcNow));
        }
        public Task<SemanticControlResult> UseAbilityAsync(string semanticInput, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorkerControlState> DetachAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task<SemanticControlResult> AimStepAsync(AimState state, CancellationToken cancellationToken)
        {
            AimStates.Add(state);
            return Task.FromResult(new SemanticControlResult("aim", [DeterministicAim.Decide(state)], SafetyCheck.Pass(), DateTimeOffset.UtcNow));
        }
        public Task<SemanticControlResult> CombatStepAsync(CombatState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
