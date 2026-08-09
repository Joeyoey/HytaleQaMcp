using System.Numerics;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Runner.Tests;

public sealed class SemanticControlsTests
{
    [Fact]
    public void SemanticUiClickUsesContainedClientPointerThenPhysicalClick()
    {
        var node = new UiSemanticNode("gear.equip", "Equip", "ready", true, true,
            0.70, 0.80, 0.10, 0.08);

        var intents = UiSemanticResolver.Click(node);

        Assert.Equal(2, intents.Count);
        Assert.Equal(PhysicalIntentKind.PointerMoveClient, intents[0].Kind);
        Assert.InRange(intents[0].X, 0.749, 0.751);
        Assert.InRange(intents[0].Y, 0.839, 0.841);
        Assert.Equal(PhysicalIntentKind.PrimaryClick, intents[1].Kind);
    }

    [Fact]
    public void SemanticUiRejectsBoundsOutsidePinnedClient()
    {
        var node = new UiSemanticNode("bad", "Bad", "ready", true, true,
            0.95, 0.95, 0.10, 0.10);
        Assert.Throws<InvalidOperationException>(() => UiSemanticResolver.Click(node));
    }
    [Fact]
    public void NavigatorTurnsBeforeMovingAndStopsAtTarget()
    {
        var turn = DeterministicNavigator.Decide(new(Vector3.Zero, 90, new(0, 0, 10), true, false, TimeSpan.Zero));
        Assert.Equal(PhysicalIntentKind.Turn, Assert.Single(turn).Kind);
        var stop = DeterministicNavigator.Decide(new(Vector3.Zero, 0, new(0.5f, 0, 0.5f), true, false, TimeSpan.Zero));
        Assert.Equal(PhysicalIntentKind.ReleaseAll, Assert.Single(stop).Kind);
    }

    [Fact]
    public void NavigatorUsesBoundedPhysicalUnstuckSequence()
    {
        var result = DeterministicNavigator.Decide(new(Vector3.Zero, 0, new(0, 0, 10), true, true, TimeSpan.FromSeconds(4)));
        Assert.Contains(result, intent => intent.Kind == PhysicalIntentKind.MoveBackward);
        Assert.Contains(result, intent => intent.Kind == PhysicalIntentKind.Jump);
        Assert.DoesNotContain(result, intent => intent.Key is "teleport" or "command");
    }

    [Fact]
    public void ReflexFiltersPlayersFriendliesOtherRunsAndNoLineOfSight()
    {
        var now = DateTimeOffset.UtcNow;
        CombatCandidate[] candidates =
        [
            new(1, new(0, 0, 3), .1, 3, true, true, true, false, true, true, 0),
            new(2, new(0, 0, 4), .2, 4, true, true, false, true, true, true, 0),
            new(3, new(0, 0, 5), .3, 5, true, true, false, false, false, true, 0),
            new(4, new(0, 0, 6), .4, 6, true, true, false, false, true, false, 0),
            new(5, new(0, 0, 7), .5, 7, false, true, false, false, true, true, 0)
        ];
        var selected = OfflineCombatReflex.SelectTarget(new(Vector3.Zero, 0, 0, now, now, now, now, 0, candidates));
        Assert.Equal(5, selected?.StableEntityId);
    }

    [Fact]
    public void ReflexOnlyEmitsPhysicalInput()
    {
        var now = DateTimeOffset.UtcNow;
        var target = new CombatCandidate(9, new(0, 0, 7), .5, 7, true, true, false, false, true, true, 0);
        var result = OfflineCombatReflex.Decide(new(Vector3.Zero, 0, 0, now, now.AddMinutes(-2), now.AddMinutes(-2), now.AddMinutes(-2), 100, [target]));
        Assert.Contains(result, intent => intent.Kind == PhysicalIntentKind.PressKey && intent.Key == "X2");
        Assert.All(result, intent => Assert.Contains(intent.Kind, Enum.GetValues<PhysicalIntentKind>()));
    }

    [Fact]
    public void EvidenceChainDetectsNoMutation()
    {
        var chain = new QaEvidenceChain();
        chain.Append("input", "one", new { key = "W" }, DateTimeOffset.UnixEpoch);
        chain.Append("packet", "one", new { kind = "movement" }, DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        Assert.True(chain.Verify());
    }

    [Fact]
    public void EvidenceReportVerificationRejectsTamperedPayload()
    {
        var chain = new QaEvidenceChain();
        chain.Append("input", "one", new { key = "W" }, DateTimeOffset.UnixEpoch);
        chain.Append("packet", "one", new { kind = "movement" }, DateTimeOffset.UnixEpoch.AddMilliseconds(1));
        var evidence = chain.Items.ToArray();
        evidence[1] = evidence[1] with
        {
            Payload = System.Text.Json.JsonSerializer.SerializeToElement(new { kind = "invented" })
        };

        Assert.False(QaEvidenceChain.Verify(evidence, out _));
    }
}
