using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Tests;

public sealed class ObserverFixtureBindingTests
{
    private static readonly Guid FixturePlayer = Guid.Parse("00000000-0000-0000-0000-000000000777");
    private static readonly Guid ActualPlayer = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public void LauncherBoundaryBindsSoleObservedPlayerInsteadOfSyntheticFixtureIdentity()
    {
        var result = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot());

        Assert.True(result.Valid);
        Assert.Equal(ActualPlayer, result.PlayerId);
        Assert.Equal(QaRunOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void DedicatedBoundaryPreservesExactFixtureUuidAndName()
    {
        var fixture = Fixture(QaPlayerIdentitySelector.ExactFixture,
            new QaPlayerFixture(ActualPlayer, "ActualPlayer", QaPlayerIdentitySelector.ExactFixture));
        Assert.True(ObserverFixtureBinding.Validate(OfflineProofKind.DockerDedicated, fixture, Snapshot()).Valid);

        var mismatch = ObserverFixtureBinding.Validate(OfflineProofKind.DockerDedicated,
            fixture with { Player = fixture.Player with { Name = "DifferentPlayer" } }, Snapshot());

        Assert.False(mismatch.Valid);
        Assert.Equal("fixture-player-name-mismatch", mismatch.Code);
        Assert.Equal(QaRunOutcome.InvalidEvidence, mismatch.Outcome);
    }

    [Fact]
    public void SpoofedScenarioProfileCannotRelaxDedicatedIdentity()
    {
        var fixture = Fixture(QaPlayerIdentitySelector.ProofBoundSolePlayer,
            new QaPlayerFixture(null, null, QaPlayerIdentitySelector.ProofBoundSolePlayer)) with
        { ServerProfile = "launcher-owned-singleplayer" };

        var result = ObserverFixtureBinding.Validate(OfflineProofKind.DockerDedicated, fixture, Snapshot());

        Assert.False(result.Valid);
        Assert.Equal("dedicated-fixture-player-required", result.Code);
        Assert.Equal(QaRunOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void MissingAndLegacyProvenanceAreBlockedWithoutSourceInference()
    {
        var missing = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot(state: new { modalVisible = false }));
        var legacy = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot(state: new
            {
                modalVisible = false,
                fixtureWorldSeed = 101L,
                fixtureRunSeed = 202L,
                fixtureSnapshotId = "snapshot-a",
                fixtureLoadoutId = "loadout-a"
            }));

        Assert.Equal(("observer-provenance-unavailable", QaRunOutcome.Blocked), (missing.Code, missing.Outcome));
        Assert.Equal(("observer-provenance-legacy-untrusted", QaRunOutcome.Blocked), (legacy.Code, legacy.Outcome));
    }

    [Fact]
    public void AuthenticatedProvenanceMismatchIsInvalidEvidence()
    {
        var result = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot(provenance: Provenance(runSeed: 999L)));

        Assert.False(result.Valid);
        Assert.Equal("fixture-provenance-mismatch", result.Code);
        Assert.Equal(QaRunOutcome.InvalidEvidence, result.Outcome);
    }

    [Fact]
    public void MissingSourceIsBlockedAndEmptySourceIsInvalidEvidence()
    {
        var incomplete = new
        {
            schema = ObserverFixtureBinding.ProvenanceSchema,
            worldSeed = 101L,
            runSeed = 202L,
            snapshotId = "snapshot-a",
            loadoutId = "loadout-a",
            runSeedSource = "director-record",
            snapshotSource = "world-snapshot",
            loadoutSource = "native-profile"
        };
        var malformed = Provenance(worldSeedSource: " ");

        var blocked = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot(provenance: incomplete));
        var invalid = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            Fixture(QaPlayerIdentitySelector.ExactFixture), Snapshot(provenance: malformed));

        Assert.Equal(("observer-provenance-incomplete", QaRunOutcome.Blocked), (blocked.Code, blocked.Outcome));
        Assert.Equal(("observer-provenance-invalid", QaRunOutcome.InvalidEvidence), (invalid.Code, invalid.Outcome));
    }

    [Fact]
    public void LauncherProofBoundWorldCanStartBeforeRunSnapshotOrLoadoutExist()
    {
        var fixture = ProofBoundFixture();
        var result = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            fixture, Snapshot(provenance: WorldOnlyProvenance()), new ObserverFixturePins());

        Assert.True(result.Valid);
        Assert.Equal(QaRunOutcome.Passed, result.Outcome);
    }

    [Fact]
    public void DedicatedBoundaryRejectsProofBoundWorldEvenWhenScenarioProfileIsSpoofed()
    {
        var fixture = ProofBoundFixture() with { ServerProfile = "hytale-qa-offline-dedicated-looking-launcher" };
        var result = ObserverFixtureBinding.Validate(OfflineProofKind.DockerDedicated,
            fixture, Snapshot(provenance: WorldOnlyProvenance()), new ObserverFixturePins());

        Assert.False(result.Valid);
        Assert.Equal("dedicated-exact-world-seed-required", result.Code);
        Assert.Equal(QaRunOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public void ObserveAndPinAcceptsLateAuthoritativeValueThenRejectsDrift()
    {
        var fixture = ProofBoundFixture();
        var pins = new ObserverFixturePins();
        Assert.True(ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            fixture, Snapshot(provenance: WorldOnlyProvenance()), pins).Valid);
        Assert.True(ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            fixture, Snapshot(provenance: RunProvenance(202L)), pins).Valid);

        var drift = ObserverFixtureBinding.Validate(OfflineProofKind.LauncherOwnedSingleplayer,
            fixture, Snapshot(provenance: RunProvenance(303L)), pins);

        Assert.False(drift.Valid);
        Assert.Equal("fixture-provenance-drift", drift.Code);
        Assert.Equal(QaRunOutcome.InvalidEvidence, drift.Outcome);
    }

    private static QaScenarioFixture Fixture(QaPlayerIdentitySelector selector, QaPlayerFixture? player = null) => new(
        "hytale-qa-offline", "snapshot-a", 101L, 202L,
        player ?? new QaPlayerFixture(FixturePlayer, "SyntheticFixture", selector),
        new QaClientFixture(1600, 900, "borderless", 1, 80, "default", "stable"), "loadout-a");

    private static QaScenarioFixture ProofBoundFixture() => new(
        "hytale-qa-offline", null, null, null,
        new QaPlayerFixture(null, null, QaPlayerIdentitySelector.ProofBoundSolePlayer),
        new QaClientFixture(1600, 900, "borderless", 1, 80, "default", "stable"), null,
        QaWorldSeedSelector.ProofBound,
        QaObservedValueSelector.ObserveAndPin,
        QaObservedValueSelector.ObserveAndPin,
        QaObservedValueSelector.ObserveAndPin);

    private static object Provenance(long runSeed = 202L, string worldSeedSource = "world-manifest") => new
    {
        schema = ObserverFixtureBinding.ProvenanceSchema,
        worldSeed = 101L,
        runSeed,
        snapshotId = "snapshot-a",
        loadoutId = "loadout-a",
        worldSeedSource,
        runSeedSource = "director-record",
        snapshotSource = "world-snapshot",
        loadoutSource = "native-profile"
    };

    private static object WorldOnlyProvenance() => new
    {
        schema = ObserverFixtureBinding.ProvenanceSchema,
        worldSeed = 101L,
        worldSeedSource = "world-manifest"
    };

    private static object RunProvenance(long runSeed) => new
    {
        schema = ObserverFixtureBinding.ProvenanceSchema,
        worldSeed = 101L,
        worldSeedSource = "world-manifest",
        runSeed,
        runSeedSource = "director-record"
    };

    private static JsonElement Snapshot(object? state = null, object? provenance = null)
    {
        state ??= new { modalVisible = false, fixtureProvenance = provenance ?? Provenance() };
        return JsonSerializer.SerializeToElement(new
        {
            playerId = ActualPlayer,
            playerName = "ActualPlayer",
            worldId = "00000000-0000-0000-0000-000000000123",
            state
        });
    }
}
