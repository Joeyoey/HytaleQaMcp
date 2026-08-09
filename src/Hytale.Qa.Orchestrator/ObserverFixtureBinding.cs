using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Orchestrator;

internal sealed record ObserverFixtureBindingResult(
    bool Valid,
    Guid? PlayerId,
    string Code,
    string Message,
    QaRunOutcome Outcome);

internal sealed class ObserverFixturePins
{
    public bool WorldSeedPinned { get; set; }
    public long WorldSeed { get; set; }
    public bool RunSeedPinned { get; set; }
    public long RunSeed { get; set; }
    public bool SnapshotPinned { get; set; }
    public string? SnapshotId { get; set; }
    public bool LoadoutPinned { get; set; }
    public string? LoadoutId { get; set; }
}

internal static class ObserverFixtureBinding
{
    internal const string ProvenanceSchema = "hytale-qa-observer-fixture-provenance-v1";

    public static ObserverFixtureBindingResult Validate(
        OfflineProofKind authenticatedBoundary,
        QaScenarioFixture fixture,
        JsonElement worldSnapshot,
        ObserverFixturePins? pins = null)
    {
        pins ??= new ObserverFixturePins();
        if (!worldSnapshot.TryGetProperty("playerId", out var playerValue) ||
            playerValue.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(playerValue.GetString(), out var playerId) || playerId == Guid.Empty)
            return Invalid("observer-player-identity-invalid",
                "Authenticated observer did not return a valid non-empty sole-player UUID.");

        if (authenticatedBoundary == OfflineProofKind.DockerDedicated)
        {
            if (fixture.WorldSeedSelector != QaWorldSeedSelector.Exact)
                return Blocked("dedicated-exact-world-seed-required",
                    "Dedicated QA requires a numeric exact world seed; proof-bound world selection is launcher-only.");
            if (fixture.Player.IdentitySelector != QaPlayerIdentitySelector.ExactFixture ||
                fixture.Player.Uuid is not { } expectedId || string.IsNullOrWhiteSpace(fixture.Player.Name))
                return Blocked("dedicated-fixture-player-required",
                    "Dedicated QA requires an exact fixture UUID and name; proof-bound launcher selection cannot relax it.");
            if (playerId != expectedId)
                return Invalid("fixture-player-mismatch",
                    "Authenticated dedicated observer player UUID contradicts the exact fixture identity.");
            if (!worldSnapshot.TryGetProperty("playerName", out var nameValue) ||
                nameValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameValue.GetString()))
                return Blocked("fixture-player-name-unavailable",
                    "Dedicated observer did not expose the exact fixture player name.");
            if (!string.Equals(nameValue.GetString(), fixture.Player.Name, StringComparison.Ordinal))
                return Invalid("fixture-player-name-mismatch",
                    "Authenticated dedicated observer player name contradicts the exact fixture identity.");
        }
        else if (authenticatedBoundary != OfflineProofKind.LauncherOwnedSingleplayer)
        {
            return Invalid("offline-proof-boundary-unsupported", "Authenticated offline proof boundary is unsupported.");
        }

        if (!worldSnapshot.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object)
            return Invalid("observer-state-invalid", "Authenticated observer state is missing or malformed.");

        var provenance = ParseProvenance(state);
        if (provenance.Status == ProvenanceStatus.Missing)
            return Blocked("observer-provenance-unavailable",
                "Authenticated observer did not expose the versioned actual-world fixture provenance contract.");
        if (provenance.Status == ProvenanceStatus.LegacyUntrusted)
            return Blocked("observer-provenance-legacy-untrusted",
                "Legacy fixture fields have no authenticated source attribution and cannot prove the actual launcher world.");
        if (provenance.Status == ProvenanceStatus.Incomplete)
            return Blocked("observer-provenance-incomplete", provenance.Error!);
        if (provenance.Status == ProvenanceStatus.Invalid)
            return Invalid("observer-provenance-invalid", provenance.Error!);

        var actual = provenance.Value!;
        var policyFailure = ValidateWorldSeed(fixture, actual, pins)
            ?? ValidateRunSeed(fixture, actual, pins)
            ?? ValidateSnapshot(fixture, actual, pins)
            ?? ValidateLoadout(fixture, actual, pins);
        if (policyFailure is not null) return policyFailure;

        return new(true, playerId, "observer-fixture-provenance-valid",
            authenticatedBoundary == OfflineProofKind.LauncherOwnedSingleplayer
                ? "Sole observed player is bound to the launcher proof and actual world provenance matches the fixture."
                : "Dedicated observer identity and actual world provenance match the exact fixture.",
            QaRunOutcome.Passed);
    }

    private static ObserverFixtureBindingResult? ValidateWorldSeed(
        QaScenarioFixture fixture, FixtureProvenance actual, ObserverFixturePins pins)
    {
        if (fixture.WorldSeedSelector == QaWorldSeedSelector.Exact)
        {
            if (fixture.WorldSeed is not { } expected)
                return Invalid("fixture-world-seed-policy-invalid", "Exact world-seed policy has no numeric expected value.");
            return actual.WorldSeed == expected ? null : Invalid("fixture-provenance-mismatch",
                "Authenticated actual world seed contradicts the exact scenario fixture.");
        }
        if (!pins.WorldSeedPinned)
        { pins.WorldSeed = actual.WorldSeed; pins.WorldSeedPinned = true; return null; }
        return pins.WorldSeed == actual.WorldSeed ? null : Invalid("fixture-provenance-drift",
            "Authenticated proof-bound world seed changed after it was pinned.");
    }

    private static ObserverFixtureBindingResult? ValidateRunSeed(
        QaScenarioFixture fixture, FixtureProvenance actual, ObserverFixturePins pins)
    {
        if (fixture.RunSeedSelector == QaObservedValueSelector.Exact)
        {
            if (fixture.RunSeed is not { } expected)
                return Invalid("fixture-run-seed-policy-invalid", "Exact run-seed policy has no numeric expected value.");
            if (!actual.RunSeedAvailable)
                return Blocked("observer-run-seed-unavailable", "Exact run seed is not authoritative yet.");
            return actual.RunSeed == expected ? null : Invalid("fixture-provenance-mismatch",
                "Authenticated actual run seed contradicts the exact scenario fixture.");
        }
        if (!actual.RunSeedAvailable) return null;
        if (!pins.RunSeedPinned)
        { pins.RunSeed = actual.RunSeed; pins.RunSeedPinned = true; return null; }
        return pins.RunSeed == actual.RunSeed ? null : Invalid("fixture-provenance-drift",
            "Authenticated run seed changed after observe-and-pin.");
    }

    private static ObserverFixtureBindingResult? ValidateSnapshot(
        QaScenarioFixture fixture, FixtureProvenance actual, ObserverFixturePins pins)
    {
        if (fixture.SnapshotSelector == QaObservedValueSelector.Exact)
        {
            if (fixture.Snapshot is null)
                return Invalid("fixture-snapshot-policy-invalid", "Exact snapshot policy has no expected snapshot id.");
            if (!actual.SnapshotAvailable)
                return Blocked("observer-snapshot-unavailable", "Exact snapshot identity is not authoritative yet.");
            return string.Equals(actual.SnapshotId, fixture.Snapshot, StringComparison.Ordinal) ? null :
                Invalid("fixture-provenance-mismatch", "Authenticated actual snapshot contradicts the exact scenario fixture.");
        }
        if (!actual.SnapshotAvailable) return null;
        if (!pins.SnapshotPinned)
        { pins.SnapshotId = actual.SnapshotId; pins.SnapshotPinned = true; return null; }
        return string.Equals(pins.SnapshotId, actual.SnapshotId, StringComparison.Ordinal) ? null :
            Invalid("fixture-provenance-drift", "Authenticated snapshot changed after observe-and-pin.");
    }

    private static ObserverFixtureBindingResult? ValidateLoadout(
        QaScenarioFixture fixture, FixtureProvenance actual, ObserverFixturePins pins)
    {
        if (fixture.LoadoutSelector == QaObservedValueSelector.Exact)
        {
            if (fixture.Loadout is null)
                return Invalid("fixture-loadout-policy-invalid", "Exact loadout policy has no expected loadout id.");
            if (!actual.LoadoutAvailable)
                return Blocked("observer-loadout-unavailable", "Exact loadout identity is not authoritative yet.");
            return string.Equals(actual.LoadoutId, fixture.Loadout, StringComparison.Ordinal) ? null :
                Invalid("fixture-provenance-mismatch", "Authenticated actual loadout contradicts the exact scenario fixture.");
        }
        if (!actual.LoadoutAvailable) return null;
        if (!pins.LoadoutPinned)
        { pins.LoadoutId = actual.LoadoutId; pins.LoadoutPinned = true; return null; }
        return string.Equals(pins.LoadoutId, actual.LoadoutId, StringComparison.Ordinal) ? null :
            Invalid("fixture-provenance-drift", "Authenticated loadout changed after observe-and-pin.");
    }

    private static ParsedProvenance ParseProvenance(JsonElement state)
    {
        if (!state.TryGetProperty("fixtureProvenance", out var value))
        {
            var legacy = new[] { "fixtureWorldSeed", "fixtureRunSeed", "fixtureSnapshotId", "fixtureLoadoutId" }
                .Any(name => state.TryGetProperty(name, out _));
            return new(legacy ? ProvenanceStatus.LegacyUntrusted : ProvenanceStatus.Missing, null, null);
        }
        if (value.ValueKind != JsonValueKind.Object)
            return new(ProvenanceStatus.Invalid, null, "fixtureProvenance must be an object.");
        if (!value.TryGetProperty("schema", out var schema))
            return new(ProvenanceStatus.Incomplete, null, "fixtureProvenance.schema is required.");
        if (schema.ValueKind != JsonValueKind.String ||
            !string.Equals(schema.GetString(), ProvenanceSchema, StringComparison.Ordinal))
            return new(ProvenanceStatus.Invalid, null, "fixtureProvenance schema is unsupported.");

        if (!TryLong(value, "worldSeed", out var worldSeed, out var error) ||
            !TrySource(value, "worldSeedSource", out var worldSeedSource, out error) ||
            !TryOptionalLong(value, "runSeed", "runSeedSource", out var runSeedAvailable, out var runSeed, out var runSeedSource, out error) ||
            !TryOptionalNullableString(value, "snapshotId", "snapshotSource", out var snapshotAvailable, out var snapshotId, out var snapshotSource, out error) ||
            !TryOptionalNullableString(value, "loadoutId", "loadoutSource", out var loadoutAvailable, out var loadoutId, out var loadoutSource, out error))
            return new(error!.StartsWith("Missing ", StringComparison.Ordinal)
                ? ProvenanceStatus.Incomplete : ProvenanceStatus.Invalid, null, error);

        return new(ProvenanceStatus.Valid,
            new(worldSeed, worldSeedSource!, runSeedAvailable, runSeed, runSeedSource,
                snapshotAvailable, snapshotId, snapshotSource, loadoutAvailable, loadoutId, loadoutSource), null);
    }

    private static bool TryLong(JsonElement parent, string name, out long value, out string? error)
    {
        value = default;
        if (!parent.TryGetProperty(name, out var field))
        { error = $"Missing fixtureProvenance.{name}."; return false; }
        if (!field.TryGetInt64(out value))
        { error = $"fixtureProvenance.{name} must be an integer."; return false; }
        error = null; return true;
    }

    private static bool TryOptionalLong(JsonElement parent, string name, string sourceName,
        out bool available, out long value, out string? source, out string? error)
    {
        available = parent.TryGetProperty(name, out var field);
        var hasSource = parent.TryGetProperty(sourceName, out _);
        value = default; source = null;
        if (!available && !hasSource) { error = null; return true; }
        if (!available || !hasSource)
        { error = $"Missing fixtureProvenance.{(!available ? name : sourceName)}."; return false; }
        if (!field.TryGetInt64(out value))
        { error = $"fixtureProvenance.{name} must be an integer."; return false; }
        return TrySource(parent, sourceName, out source, out error);
    }

    private static bool TryOptionalNullableString(JsonElement parent, string name, string sourceName,
        out bool available, out string? value, out string? source, out string? error)
    {
        available = parent.TryGetProperty(name, out var field);
        var hasSource = parent.TryGetProperty(sourceName, out _);
        value = null; source = null;
        if (!available && !hasSource) { error = null; return true; }
        if (!available || !hasSource)
        { error = $"Missing fixtureProvenance.{(!available ? name : sourceName)}."; return false; }
        if (field.ValueKind == JsonValueKind.String) value = field.GetString();
        else if (field.ValueKind != JsonValueKind.Null)
        { error = $"fixtureProvenance.{name} must be a string or null."; return false; }
        return TrySource(parent, sourceName, out source, out error);
    }

    private static bool TrySource(JsonElement parent, string name, out string? value, out string? error)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var field))
        { error = $"Missing fixtureProvenance.{name}."; return false; }
        if (field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
        { error = $"fixtureProvenance.{name} must be a non-empty explicit source."; return false; }
        value = field.GetString(); error = null; return true;
    }

    private static ObserverFixtureBindingResult Blocked(string code, string message) =>
        new(false, null, code, message, QaRunOutcome.Blocked);

    private static ObserverFixtureBindingResult Invalid(string code, string message) =>
        new(false, null, code, message, QaRunOutcome.InvalidEvidence);

    private enum ProvenanceStatus { Missing, LegacyUntrusted, Incomplete, Invalid, Valid }

    private sealed record ParsedProvenance(ProvenanceStatus Status, FixtureProvenance? Value, string? Error);

    private sealed record FixtureProvenance(
        long WorldSeed,
        string WorldSeedSource,
        bool RunSeedAvailable,
        long RunSeed,
        string? RunSeedSource,
        bool SnapshotAvailable,
        string? SnapshotId,
        string? SnapshotSource,
        bool LoadoutAvailable,
        string? LoadoutId,
        string? LoadoutSource);
}
