using System.Numerics;
using System.Text.Json;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Orchestrator;

public sealed record ObserverControlResult(
    string Action,
    bool Executed,
    bool ObjectiveReached,
    long ObservationSequence,
    string WorldId,
    string Message);

public sealed class AuthenticatedObserverControls(IWorkerControlService worker)
{
    private const int MaximumInteractionAimSteps = 8;
    private const double InteractionAimToleranceDegrees = 3;
    private readonly SemaphoreSlim navigationGate = new(1, 1);
    private NavigationProgress? navigationProgress;

    public async Task<ObserverControlResult> NavigateAsync(string targetKind, string? targetId,
        double within, CancellationToken cancellationToken)
        => await NavigateAsync(targetKind, targetId, null, within, cancellationToken).ConfigureAwait(false);

    public async Task<ObserverControlResult> NavigateAsync(string targetKind, string? targetId,
        string? targetState, double within, CancellationToken cancellationToken)
    {
        await navigationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var observation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
            var state = ObservedState.Parse(observation);
            var resolved = state.ResolveNavigationTarget(targetKind, targetId, targetState);
            var target = resolved.Position;
            var distance = Vector3.Distance(state.Position, target);
            var now = observation.ObservedAt;
            var progressKey = $"{state.WorldId}|{resolved.Key}";
            if (navigationProgress is null || !string.Equals(navigationProgress.Key, progressKey, StringComparison.Ordinal) ||
                PlanarDistance(navigationProgress.Target, target) > 0.5f || now < navigationProgress.LastObservedAt)
                navigationProgress = new(progressKey, target, state.Position, now, now, false, 0);

            var history = navigationProgress;
            var planarProgress = PlanarDistance(history.LastPosition, state.Position);
            var insufficientPulses = history.InsufficientForwardPulses;
            var lastProgressAt = history.LastProgressAt;
            if (planarProgress >= 0.08f)
            {
                lastProgressAt = now;
                insufficientPulses = 0;
            }
            else if (history.ForwardPulseSent && state.Grounded)
                insufficientPulses++;
            else if (!state.Grounded)
                insufficientPulses = 0;

            if (distance <= Math.Clamp(within, 0.5, 8))
            {
                navigationProgress = null;
                return Result("navigate", false, true, observation, "Authenticated target is within range.");
            }

            var noProgress = now - lastProgressAt;
            if (noProgress < TimeSpan.Zero) noProgress = TimeSpan.Zero;
            var lineOfTravelBlocked = state.Grounded && insufficientPulses >= 2;
            var control = await worker.NavigateStepAsync(new(state.Position, state.YawDegrees, target, state.Grounded,
                lineOfTravelBlocked, noProgress), cancellationToken).ConfigureAwait(false);
            var forwardPulse = control.Intents.Any(intent => intent.Kind == PhysicalIntentKind.MoveForward);
            navigationProgress = new(progressKey, target, state.Position, now, lastProgressAt,
                forwardPulse, insufficientPulses);
            return Result("navigate", true, false, observation,
                $"Executed one observer-derived navigation step (noProgress={noProgress.TotalMilliseconds:F0}ms, blocked={lineOfTravelBlocked}).");
        }
        finally { navigationGate.Release(); }
    }

    public async Task<ObserverControlResult> InteractAsync(string targetKind, string? targetId,
        CancellationToken cancellationToken)
        => await InteractAsync(targetKind, targetId, null, cancellationToken).ConfigureAwait(false);

    public async Task<ObserverControlResult> InteractAsync(string targetKind, string? targetId,
        string? targetState, CancellationToken cancellationToken)
    {
        ObserverObservation observation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        var aimSteps = 0;
        while (true)
        {
            var state = ObservedState.Parse(observation);
            var target = state.ResolveTarget(targetKind, targetId, targetState).Position;
            var aim = new AimState(state.Eye, state.YawDegrees, state.PitchDegrees, target);
            if (DeterministicAim.IsAligned(aim, InteractionAimToleranceDegrees)) break;
            if (aimSteps >= MaximumInteractionAimSteps)
                return Result("interact", aimSteps > 0, false, observation,
                    "Refused interaction because observer-derived aim did not converge.");
            await worker.AimStepAsync(aim, cancellationToken).ConfigureAwait(false);
            aimSteps++;
            await Task.Delay(75, cancellationToken).ConfigureAwait(false);
            observation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        }
        await worker.InteractAsync("left_click", cancellationToken).ConfigureAwait(false);
        return Result("interact", true, false, observation,
            $"Aligned in {aimSteps} observer-derived aim step(s) and interacted using authenticated target coordinates.");
    }

    public async Task<ObserverControlResult> AimAsync(string targetKind, string? targetId,
        CancellationToken cancellationToken)
        => await AimAsync(targetKind, targetId, null, cancellationToken).ConfigureAwait(false);

    public async Task<ObserverControlResult> AimAsync(string targetKind, string? targetId,
        string? targetState, CancellationToken cancellationToken)
    {
        var observation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        var state = ObservedState.Parse(observation);
        var target = state.ResolveTarget(targetKind, targetId, targetState).Position;
        await worker.AimStepAsync(new(state.Eye, state.YawDegrees, state.PitchDegrees, target), cancellationToken)
            .ConfigureAwait(false);
        return Result("aim", true, false, observation, "Aimed using authenticated target coordinates.");
    }

    public async Task<ObserverControlResult> AimAttackAsync(string authenticatedRole,
        CancellationToken cancellationToken)
    {
        var observation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        var state = ObservedState.Parse(observation);
        var target = state.ResolveAttackTarget(authenticatedRole).Position;
        await worker.AimStepAsync(new(state.Eye, state.YawDegrees, state.PitchDegrees, target), cancellationToken)
            .ConfigureAwait(false);
        return Result("aim", true, false, observation, "Aimed at an authenticated hostile current-run entity.");
    }

    public async Task<ObserverControlResult> CombatAsync(CancellationToken cancellationToken) =>
        await CombatAsync(null, cancellationToken).ConfigureAwait(false);

    public async Task<ObserverControlResult> CombatAsync(string? authenticatedRole, CancellationToken cancellationToken)
    {
        var first = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        var state = ObservedState.Parse(first);
        var target = state.Encounters.Where(IsAttackableEncounter)
            .Where(value => authenticatedRole is null || RoleMatches(value.Role, authenticatedRole))
            .OrderBy(value => value.Distance).ThenBy(value => value.StableEntityId).FirstOrDefault();
        if (target is null) return Result("combat", false, true, first, "No authenticated current-run hostile remains.");
        await worker.AimStepAsync(new(state.Eye, state.YawDegrees, state.PitchDegrees, target.Position), cancellationToken)
            .ConfigureAwait(false);
        await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        var aimedObservation = await worker.ObserveSolePlayerAsync(cancellationToken).ConfigureAwait(false);
        var aimed = ObservedState.Parse(aimedObservation);
        var candidates = aimed.Encounters.Select(value => value.ToCandidate()).ToArray();
        if (!candidates.Any(value => value.StableEntityId == target.StableEntityId && value.LineOfSight))
            return Result("combat", true, false, aimedObservation, "Target was not authenticated under the crosshair; no attack was sent.");
        await worker.CombatStepAsync(aimed.Combat(candidates), cancellationToken).ConfigureAwait(false);
        return Result("combat", true, false, aimedObservation, "Executed one combat-reflex step against an authenticated current-run hostile.");
    }

    internal static bool RoleMatches(string actual, string requested)
    {
        var normalized = string.Concat(requested.Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()))
            .Replace('-', '_');
        return string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase) ||
               actual.StartsWith(normalized + ".", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsAttackableEncounter(ObservedEncounter encounter) =>
        encounter.Alive && encounter.Hostile && encounter.CurrentRun &&
        !encounter.IsPlayer && !encounter.FriendlyNpc;

    private static ObserverControlResult Result(string action, bool executed, bool reached,
        ObserverObservation observation, string message) => new(action, executed, reached,
        observation.ObservationSequence, observation.WorldSnapshot.GetProperty("worldId").GetString() ?? "", message);

    private static float PlanarDistance(Vector3 left, Vector3 right) =>
        Vector2.Distance(new(left.X, left.Z), new(right.X, right.Z));

    private sealed record NavigationProgress(string Key, Vector3 Target, Vector3 LastPosition,
        DateTimeOffset LastObservedAt, DateTimeOffset LastProgressAt, bool ForwardPulseSent,
        int InsufficientForwardPulses);
}

internal sealed record ObservedSemanticTarget(
    string Id, string Role, string RoomId, int RoomIndex, Vector3 Position);

internal sealed record ResolvedObservedTarget(string Key, Vector3 Position);

internal sealed record ObservedEncounter(
    long StableEntityId, Vector3 Position, double HealthFraction, double Distance,
    bool Alive, bool Hostile, bool CurrentRun, bool IsPlayer, bool FriendlyNpc, bool CrosshairTarget,
    string Role, string RunId, int RoomIndex, string ObjectiveId, string EvidenceId)
{
    public CombatCandidate ToCandidate() => new(StableEntityId, Position, HealthFraction, Distance,
        false, Hostile, IsPlayer, FriendlyNpc, CurrentRun, CrosshairTarget,
        CrosshairTarget ? 0 : 180);
}

internal sealed record ObservedState(
    Vector3 Position, Vector3 Eye, double YawDegrees, double PitchDegrees, bool Dead,
    bool Grounded, bool Jumping, bool Falling, string WorldId, string CurrentRoomId,
    string CurrentObjectiveId, JsonElement State,
    IReadOnlyList<ObservedSemanticTarget> SemanticTargets,
    IReadOnlyList<ObservedEncounter> Encounters, int SignatureEnergy,
    IReadOnlyDictionary<string, long> Cooldowns)
{
    public static ObservedState Parse(ObserverObservation observation)
    {
        if (!observation.EvidenceValid) throw new InvalidDataException("Authenticated observer evidence is invalid.");
        var world = observation.WorldSnapshot;
        if (world.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("World observation schema is unsupported.");
        var state = world.GetProperty("state");
        var position = new Vector3(Number(state, "positionX"), Number(state, "positionY"), Number(state, "positionZ"));
        var yaw = Angle(Number(state, "headYaw"));
        var pitch = Angle(Number(state, "headPitch"));
        var semanticTargets = new List<ObservedSemanticTarget>();
        if (state.TryGetProperty("semanticTargets", out var targetValues) && targetValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in targetValues.EnumerateArray())
                semanticTargets.Add(new(OptionalString(value, "id"), OptionalString(value, "role"),
                    OptionalString(value, "roomId"), OptionalInt(value, "roomIndex"),
                    new(Number(value, "positionX"), Number(value, "positionY"), Number(value, "positionZ"))));
        }
        var encounters = new List<ObservedEncounter>();
        if (state.TryGetProperty("encounters", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                var target = new Vector3(Number(value, "positionX"), Number(value, "positionY"), Number(value, "positionZ"));
                encounters.Add(new(Long(value, "stableEntityId"), target, Double(value, "healthFraction"),
                    Vector3.Distance(position, target), Boolean(value, "alive"), Boolean(value, "hostile"),
                    Boolean(value, "currentRun"), Boolean(value, "isPlayer"), Boolean(value, "friendlyNpc"),
                    Boolean(value, "crosshairTarget"), OptionalString(value, "role"), OptionalString(value, "runId"),
                    OptionalInt(value, "roomIndex"), OptionalString(value, "objectiveId"), OptionalString(value, "evidenceId")));
            }
        }
        var cooldowns = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (state.TryGetProperty("cooldownRemainingMillis", out var cooldownElement) && cooldownElement.ValueKind == JsonValueKind.Object)
            foreach (var property in cooldownElement.EnumerateObject()) cooldowns[property.Name] = property.Value.GetInt64();
        return new(position, position + new Vector3(0, 1.6f, 0), yaw, pitch,
            OptionalBoolean(state, "dead"), OptionalBoolean(state, "grounded", true),
            OptionalBoolean(state, "jumping"), OptionalBoolean(state, "falling"),
            world.GetProperty("worldId").GetString() ?? "", OptionalString(state, "currentRoomId"),
            OptionalString(state, "currentObjectiveId"), state.Clone(), semanticTargets, encounters,
            OptionalInt(state, "signatureEnergy"), cooldowns);
    }

    public ResolvedObservedTarget ResolveTarget(string kind, string? id, string? requestedState = null)
    {
        if (string.Equals(kind, "anchor", StringComparison.Ordinal) ||
            string.Equals(kind, "block", StringComparison.Ordinal) ||
            string.Equals(kind, "objective", StringComparison.Ordinal))
        {
            var semanticId = NormalizeSemanticTargetId(id);
            var semantic = string.IsNullOrWhiteSpace(semanticId)
                ? []
                : SemanticTargets.Where(target =>
                    string.Equals(target.Id, semanticId, StringComparison.OrdinalIgnoreCase)).ToArray();
            var semanticRequired = !string.Equals(kind, "objective", StringComparison.Ordinal) ||
                HasSemanticPrefix(id) || semantic.Length > 0;
            if (semanticRequired)
            {
                if (semantic.Length == 0) throw new InvalidOperationException("observer-semantic-target-unavailable");
                if (semantic.Length != 1) throw new InvalidOperationException("observer-semantic-target-not-unique");
                return new($"semantic:{semantic[0].RoomId}:{semantic[0].Id}", semantic[0].Position);
            }
            if (!string.Equals(kind, "objective", StringComparison.Ordinal))
                throw new InvalidOperationException("observer-semantic-target-unavailable");
            if (id is not null && id.StartsWith("room.", StringComparison.Ordinal))
            {
                var requestedRoom = id[5..];
                if (!string.Equals(CurrentRoomId, requestedRoom, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("observer-room-target-is-not-current-room");
            }
            else if (!string.IsNullOrWhiteSpace(id) &&
                     !string.Equals(id, CurrentObjectiveId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("observer-objective-id-does-not-match-authoritative-objective");
            if (!State.TryGetProperty("objectiveWorldX", out var x) ||
                !State.TryGetProperty("objectiveWorldY", out var y) ||
                !State.TryGetProperty("objectiveWorldZ", out var z))
                throw new InvalidOperationException("observer-objective-target-unavailable");
            return new($"objective:{CurrentRoomId}:{CurrentObjectiveId}",
                new((float)x.GetDouble(), (float)y.GetDouble(), (float)z.GetDouble()));
        }
        if (string.Equals(kind, "gate", StringComparison.Ordinal))
        {
            if (!State.TryGetProperty("gates", out var gates) || gates.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("observer-gate-target-unavailable");
            var matches = gates.EnumerateArray().Where(gate =>
            {
                var state = OptionalString(gate, "state");
                if (!string.IsNullOrWhiteSpace(requestedState) &&
                    !string.Equals(state, requestedState, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.Equals(id, "active", StringComparison.OrdinalIgnoreCase))
                    return IsActionableGateState(state);
                return id is null || string.Equals(OptionalString(gate, "gateId"), id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(OptionalString(gate, "semanticId"), id, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(OptionalString(gate, "regionId"), id, StringComparison.OrdinalIgnoreCase);
            }).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("observer-gate-target-unavailable");
            if (matches.Length != 1) throw new InvalidOperationException("observer-gate-target-not-unique");
            return new($"gate:{OptionalString(matches[0], "gateId")}",
                new(Number(matches[0], "centerX"), Number(matches[0], "centerY"), Number(matches[0], "centerZ")));
        }
        if (string.Equals(kind, "fallback", StringComparison.Ordinal))
        {
            if (!State.TryGetProperty("fallbackWorldX", out var x) ||
                !State.TryGetProperty("fallbackWorldY", out var y) ||
                !State.TryGetProperty("fallbackWorldZ", out var z))
                throw new InvalidOperationException("observer-fallback-target-unavailable");
            return new($"fallback:{CurrentRoomId}",
                new((float)x.GetDouble(), (float)y.GetDouble(), (float)z.GetDouble()));
        }
        if (string.Equals(kind, "entity", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(id))
        {
            var matches = Encounters.Where(value => value.Alive && value.CurrentRun &&
                    AuthenticatedObserverControls.RoleMatches(value.Role, id))
                .OrderBy(value => value.Distance).ThenBy(value => value.StableEntityId).ToArray();
            if (matches.Length == 0) throw new InvalidOperationException("observer-entity-role-not-found");
            return new($"entity:{matches[0].StableEntityId}", matches[0].Position);
        }
        throw new InvalidOperationException("observer-target-kind-unsupported");
    }

    public ResolvedObservedTarget ResolveNavigationTarget(
        string kind,
        string? id,
        string? requestedState = null
    ) {
        var target = ResolveTarget(kind, id, requestedState);
        if (!string.Equals(kind, "gate", StringComparison.Ordinal)
            || !State.TryGetProperty("gates", out var gates)
            || gates.ValueKind != JsonValueKind.Array) return target;

        var gateId = target.Key.StartsWith("gate:", StringComparison.Ordinal)
            ? target.Key[5..]
            : "";
        var gate = gates.EnumerateArray().SingleOrDefault(value =>
            string.Equals(OptionalString(value, "gateId"), gateId,
                StringComparison.OrdinalIgnoreCase));
        if (gate.ValueKind == JsonValueKind.Undefined
            || !gate.TryGetProperty("approaches", out var approaches)
            || approaches.ValueKind != JsonValueKind.Array
            || approaches.GetArrayLength() == 0) return target;

        var candidates = approaches.EnumerateArray()
            .Select((value, index) => new
            {
                Index = index,
                Position = new Vector3(Number(value, "x"), Number(value, "y"), Number(value, "z"))
            })
            .OrderBy(value => Vector2.Distance(
                new(Position.X, Position.Z),
                new(value.Position.X, value.Position.Z)))
            .ThenBy(value => value.Index)
            .ToArray();
        var selected = candidates[0];
        return new($"{target.Key}:approach:{selected.Index}", selected.Position);
    }

    public ResolvedObservedTarget ResolveAttackTarget(string authenticatedRole)
    {
        if (string.IsNullOrWhiteSpace(authenticatedRole))
            throw new InvalidOperationException("observer-entity-role-not-found");
        var matches = Encounters.Where(AuthenticatedObserverControls.IsAttackableEncounter)
            .Where(value => AuthenticatedObserverControls.RoleMatches(value.Role, authenticatedRole))
            .OrderBy(value => value.Distance).ThenBy(value => value.StableEntityId).ToArray();
        if (matches.Length == 0) throw new InvalidOperationException("observer-entity-role-not-found");
        return new($"entity:{matches[0].StableEntityId}", matches[0].Position);
    }

    private static bool IsActionableGateState(string state) =>
        state.Equals("OPEN", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ENTERED", StringComparison.OrdinalIgnoreCase) ||
        state.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    private static bool HasSemanticPrefix(string? id) =>
        id is not null && id.StartsWith("anchor.", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSemanticTargetId(string? id) =>
        HasSemanticPrefix(id) ? id![7..] : id;

    public CombatState Combat(IReadOnlyList<CombatCandidate> candidates)
    {
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset Last(string key, int cooldownSeconds) => Cooldowns.GetValueOrDefault(key) > 0
            ? now : now - TimeSpan.FromSeconds(cooldownSeconds);
        return new(Eye, YawDegrees, PitchDegrees, now, Last("SIGNATURE", 4), Last("UTILITY", 12),
            Last("ULTIMATE", 60), SignatureEnergy, candidates);
    }

    private static double Angle(float value) => Math.Abs(value) <= Math.PI * 2.1 ? value * 180 / Math.PI : value;
    private static float Number(JsonElement element, string name) => checked((float)Double(element, name));
    private static double Double(JsonElement element, string name) => element.GetProperty(name).GetDouble();
    private static long Long(JsonElement element, string name) => element.GetProperty(name).GetInt64();
    private static bool Boolean(JsonElement element, string name) => element.GetProperty(name).GetBoolean();
    private static bool OptionalBoolean(JsonElement element, string name, bool fallback = false) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static int OptionalInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;
    private static string OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
