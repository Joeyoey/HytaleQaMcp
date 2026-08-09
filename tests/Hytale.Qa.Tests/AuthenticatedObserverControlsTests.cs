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
        Assert.Equal(new Vector3(10, 20, 30), state.ResolveTarget("objective", "room.room_alpha").Position);
        Assert.Equal(new Vector3(11, 21, 31), state.ResolveTarget("fallback", null).Position);
        Assert.Equal(new Vector3(71, 81, 91), state.ResolveTarget("gate", "active").Position);
        Assert.Throws<InvalidOperationException>(() => state.ResolveTarget("objective", "room.room_beta"));
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

    private static ObserverObservation NavigationObservation(long sequence, DateTimeOffset observedAt, string worldId) =>
        Observation(sequence, observedAt, worldId, new
        {
            positionX = 0, positionY = 4, positionZ = 0, headYaw = 0, headPitch = 0,
            grounded = true, jumping = false, falling = false,
            currentRoomId = "room_alpha", currentObjectiveId = "objective.exit",
            objectiveWorldX = 0, objectiveWorldY = 9, objectiveWorldZ = 20,
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
        public Task<SemanticControlResult> InteractAsync(string semanticInput, CancellationToken cancellationToken) => throw new NotSupportedException();
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
        public Task<SemanticControlResult> AimStepAsync(AimState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SemanticControlResult> CombatStepAsync(CombatState state, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseAllAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
