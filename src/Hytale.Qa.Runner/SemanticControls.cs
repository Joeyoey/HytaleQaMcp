using System.Numerics;

namespace Hytale.Qa.Runner;

public enum PhysicalIntentKind
{
    ReleaseAll, MoveForward, MoveBackward, StrafeLeft, StrafeRight, Jump, Turn, Look,
    PointerMoveClient, PrimaryClick, SecondaryClick, PressKey
}
public sealed record PhysicalIntent(PhysicalIntentKind Kind, double X = 0, double Y = 0, string? Key = null, int DurationMilliseconds = 0);

public sealed record NavigationState(Vector3 Position, double YawDegrees, Vector3 Target, bool Grounded, bool LineOfTravelBlocked, TimeSpan NoProgress);

public static class DeterministicNavigator
{
    public static IReadOnlyList<PhysicalIntent> Decide(NavigationState state)
    {
        var delta = state.Target - state.Position;
        var horizontal = new Vector2(delta.X, delta.Z);
        if (horizontal.Length() <= 0.8f) return [new(PhysicalIntentKind.ReleaseAll)];
        // Hytale 0.5.x Transform.getDirection(pitch, yaw) projects forward as
        // x = -cos(pitch) * sin(yaw), z = -cos(pitch) * cos(yaw).
        var desired = Math.Atan2(-delta.X, -delta.Z) * 180 / Math.PI;
        var error = Normalize(desired - state.YawDegrees);
        if (state.NoProgress >= TimeSpan.FromSeconds(4))
            return [new(PhysicalIntentKind.ReleaseAll), new(PhysicalIntentKind.MoveBackward, DurationMilliseconds: 250), new(PhysicalIntentKind.StrafeRight, DurationMilliseconds: 350), new(PhysicalIntentKind.Jump, DurationMilliseconds: 80), new(PhysicalIntentKind.Turn, Math.CopySign(35, error == 0 ? -1 : -error))];
        if (Math.Abs(error) > 8)
            // Positive relative mouse X turns right, which decreases Hytale yaw.
            return [new(PhysicalIntentKind.Turn, Math.Clamp(-error, -20, 20))];
        if (state.LineOfTravelBlocked && state.Grounded)
            return [new(PhysicalIntentKind.Jump, DurationMilliseconds: 80), new(PhysicalIntentKind.MoveForward, DurationMilliseconds: 300)];
        return [new(PhysicalIntentKind.MoveForward, DurationMilliseconds: 100)];
    }

    public static double Normalize(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }
}

public sealed record AimState(Vector3 Eye, double YawDegrees, double PitchDegrees, Vector3 Target);

public static class DeterministicAim
{
    public static PhysicalIntent Decide(AimState state, double pixelsPerDegree = 12)
    {
        var delta = state.Target - state.Eye;
        var horizontal = Math.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        var desiredYaw = Math.Atan2(-delta.X, -delta.Z) * 180 / Math.PI;
        var desiredPitch = -Math.Atan2(delta.Y, horizontal) * 180 / Math.PI;
        var yawError = DeterministicNavigator.Normalize(desiredYaw - state.YawDegrees);
        var pitchError = Math.Clamp(desiredPitch - state.PitchDegrees, -89, 89);
        return new(PhysicalIntentKind.Look,
            Math.Round(Math.Clamp(-yawError * pixelsPerDegree, -240, 240)),
            Math.Round(Math.Clamp(pitchError * pixelsPerDegree, -180, 180)));
    }
}

public sealed record CombatCandidate(long StableEntityId, Vector3 Position, double HealthFraction, double Distance, bool AttackingPlayer, bool Hostile, bool IsPlayer, bool FriendlyNpc, bool CurrentRun, bool LineOfSight, double AngularErrorDegrees);
public sealed record CombatState(Vector3 Eye, double YawDegrees, double PitchDegrees, DateTimeOffset Now, DateTimeOffset LastPrimaryAt, DateTimeOffset LastUtilityAt, DateTimeOffset LastUltimateAt, int SignatureEnergy, IReadOnlyList<CombatCandidate> Candidates);

public static class OfflineCombatReflex
{
    public static CombatCandidate? SelectTarget(CombatState state) => state.Candidates
        .Where(target => target.CurrentRun && target.Hostile && !target.IsPlayer && !target.FriendlyNpc && target.LineOfSight && target.Distance <= 24 && Math.Abs(target.AngularErrorDegrees) <= 75)
        .OrderByDescending(target => target.AttackingPlayer)
        .ThenBy(target => target.HealthFraction)
        .ThenBy(target => target.Distance)
        .ThenBy(target => target.StableEntityId)
        .FirstOrDefault();

    public static IReadOnlyList<PhysicalIntent> Decide(CombatState state)
    {
        var target = SelectTarget(state);
        if (target is null) return [new(PhysicalIntentKind.ReleaseAll)];
        var intents = new List<PhysicalIntent> { DeterministicAim.Decide(new(state.Eye, state.YawDegrees, state.PitchDegrees, target.Position)) };
        if (state.SignatureEnergy >= 100 && state.Now - state.LastUltimateAt >= TimeSpan.FromSeconds(60))
            intents.Add(new(PhysicalIntentKind.PressKey, Key: "X2", DurationMilliseconds: 40));
        else if (state.Now - state.LastUtilityAt >= TimeSpan.FromSeconds(12) && target.Distance <= 12)
            intents.Add(new(PhysicalIntentKind.SecondaryClick, DurationMilliseconds: 40));
        else if (state.Now - state.LastPrimaryAt >= TimeSpan.FromSeconds(4))
            intents.Add(new(PhysicalIntentKind.PrimaryClick, DurationMilliseconds: 40));
        return intents;
    }
}

public sealed record UiSemanticLocator(string? Id, string? Text, string? State);
/// <summary>
/// A server-authored, authenticated UI target. Bounds are normalized to the
/// pinned Hytale client area (0..1), so callers never provide screen or world
/// coordinates and the worker can independently contain the pointer.
/// </summary>
public sealed record UiSemanticNode(
    string Id,
    string Text,
    string State,
    bool Visible,
    bool Enabled,
    double X,
    double Y,
    double Width,
    double Height,
    string? SubjectId = null,
    string? Operation = null,
    long Revision = 0);

public static class UiSemanticResolver
{
    public static UiSemanticNode Resolve(UiSemanticLocator locator, IEnumerable<UiSemanticNode> nodes)
    {
        var matches = nodes.Where(node => node.Visible && node.Enabled)
            .Where(node => locator.Id is null || node.Id == locator.Id)
            .Where(node => locator.Text is null || node.Text.Equals(locator.Text, StringComparison.OrdinalIgnoreCase))
            .Where(node => locator.State is null || node.State == locator.State)
            .OrderBy(node => node.Id, StringComparer.Ordinal)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("ui-locator-not-found"),
            _ => throw new InvalidOperationException("ui-locator-ambiguous")
        };
    }

    public static PhysicalIntent Pointer(UiSemanticNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var x = node.X + node.Width / 2;
        var y = node.Y + node.Height / 2;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1 ||
            node.Width <= 0 || node.Height <= 0 || node.X < 0 || node.Y < 0 ||
            node.X + node.Width > 1 || node.Y + node.Height > 1)
            throw new InvalidOperationException("ui-node-bounds-invalid");
        return new(PhysicalIntentKind.PointerMoveClient, x, y);
    }

    public static IReadOnlyList<PhysicalIntent> Click(UiSemanticNode node) =>
        [Pointer(node), new(PhysicalIntentKind.PrimaryClick, DurationMilliseconds: 40)];
}
