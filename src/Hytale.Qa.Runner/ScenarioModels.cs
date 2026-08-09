using System.Security.Cryptography;
using System.Text.Json;

namespace Hytale.Qa.Runner;

public sealed record QaScenario(
    string Schema,
    string Id,
    string? Description,
    IReadOnlyList<string> Tags,
    string Mode,
    IReadOnlySet<string> Capabilities,
    QaScenarioFixture Fixture,
    QaScenarioSafety Safety,
    QaScenarioBudgets Budgets,
    IReadOnlyList<QaScenarioStep> Steps,
    JsonElement Artifacts,
    string CanonicalSha256);

public sealed record QaScenarioFixture(
    string ServerProfile,
    string? Snapshot,
    long? WorldSeed,
    long? RunSeed,
    QaPlayerFixture Player,
    QaClientFixture Client,
    string? Loadout,
    QaWorldSeedSelector WorldSeedSelector = QaWorldSeedSelector.Exact,
    QaObservedValueSelector RunSeedSelector = QaObservedValueSelector.Exact,
    QaObservedValueSelector SnapshotSelector = QaObservedValueSelector.Exact,
    QaObservedValueSelector LoadoutSelector = QaObservedValueSelector.Exact);

public enum QaWorldSeedSelector { Exact, ProofBound }
public enum QaObservedValueSelector { Exact, ObserveAndPin }

public enum QaPlayerIdentitySelector
{
    ExactFixture,
    ProofBoundSolePlayer
}

public sealed record QaPlayerFixture(
    Guid? Uuid,
    string? Name,
    QaPlayerIdentitySelector IdentitySelector = QaPlayerIdentitySelector.ExactFixture);

public sealed record QaClientFixture(
    int Width,
    int Height,
    string DisplayMode,
    double HudScale,
    double Fov,
    string KeybindProfile,
    string? GraphicsProfile);

public sealed record QaScenarioSafety(
    bool OfflineRequired,
    bool LoopbackOnly,
    int MaximumClientProcesses,
    bool AbortOnFocusLoss,
    bool AbortOnEndpointDrift,
    IReadOnlySet<string> ForbiddenCapabilities);

public sealed record QaScenarioBudgets(
    int TotalSeconds,
    int StepSeconds,
    int NoProgressSeconds,
    int MaximumDeaths,
    int MaximumRecoveries);

public sealed record QaRecovery(string Policy, int MaximumAttempts);

public sealed record QaScenarioStep(
    string Id,
    string Operation,
    JsonElement? Target,
    JsonElement? Arguments,
    JsonElement? Expect,
    int? TimeoutSeconds,
    string? IdempotencyKey,
    QaRecovery? Recovery);

public static class QaScenarioLoader
{
    public static QaScenario Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "hytale-qa/v1")
            throw new InvalidDataException("scenario-schema-unsupported");
        var fixture = root.GetProperty("fixture");
        var player = fixture.GetProperty("player");
        var client = fixture.GetProperty("client");
        var safety = root.GetProperty("safety");
        var budgets = root.TryGetProperty("budgets", out var budgetElement)
            ? budgetElement
            : default;
        return new(
            "hytale-qa/v1",
            RequiredString(root, "id"),
            OptionalString(root, "description"),
            StringList(root, "tags"),
            RequiredString(root, "mode"),
            Strings(root, "capabilities"),
            new(
                RequiredString(fixture, "serverProfile"),
                OptionalString(fixture, "snapshot"),
                OptionalLong(fixture, "worldSeed"),
                OptionalLong(fixture, "runSeed"),
                ParsePlayer(player),
                new(
                    client.GetProperty("width").GetInt32(),
                    client.GetProperty("height").GetInt32(),
                    RequiredString(client, "displayMode"),
                    client.GetProperty("hudScale").GetDouble(),
                    client.GetProperty("fov").GetDouble(),
                    RequiredString(client, "keybindProfile"),
                    OptionalString(client, "graphicsProfile")),
                OptionalString(fixture, "loadout"),
                ParseWorldSeedSelector(fixture),
                ParseObservedSelector(fixture, "runSeed", "runSeedSelector"),
                ParseObservedSelector(fixture, "snapshot", "snapshotSelector"),
                ParseObservedSelector(fixture, "loadout", "loadoutSelector")),
            new(
                safety.GetProperty("offlineRequired").GetBoolean(),
                safety.GetProperty("loopbackOnly").GetBoolean(),
                safety.GetProperty("maximumClientProcesses").GetInt32(),
                safety.GetProperty("abortOnFocusLoss").GetBoolean(),
                safety.GetProperty("abortOnEndpointDrift").GetBoolean(),
                Strings(safety, "forbiddenCapabilities")),
            new(
                OptionalInt(budgets, "totalSeconds", 1800),
                OptionalInt(budgets, "stepSeconds", 180),
                OptionalInt(budgets, "noProgressSeconds", 20),
                OptionalInt(budgets, "maximumDeaths", 0),
                OptionalInt(budgets, "maximumRecoveries", 0)),
            root.GetProperty("steps").EnumerateArray().Select(ParseStep).ToArray(),
            root.GetProperty("artifacts").Clone(),
            CanonicalSha256(root));
    }

    private static QaScenarioStep ParseStep(JsonElement step)
    {
        QaRecovery? recovery = null;
        if (step.TryGetProperty("recovery", out var value))
            recovery = new(RequiredString(value, "policy"), value.GetProperty("maximumAttempts").GetInt32());
        return new(
            RequiredString(step, "id"),
            RequiredString(step, "operation"),
            Clone(step, "target"),
            Clone(step, "arguments"),
            Clone(step, "expect"),
            step.TryGetProperty("timeoutSeconds", out value) ? value.GetInt32() : null,
            step.TryGetProperty("idempotencyKey", out value) ? value.GetString() : null,
            recovery);
    }

    private static QaPlayerFixture ParsePlayer(JsonElement player)
    {
        var selector = OptionalString(player, "identitySelector") switch
        {
            null or "exact_fixture" => QaPlayerIdentitySelector.ExactFixture,
            "proof_bound_sole_player" => QaPlayerIdentitySelector.ProofBoundSolePlayer,
            var value => throw new InvalidDataException($"scenario-player-identity-selector-unsupported:{value}")
        };
        if (selector == QaPlayerIdentitySelector.ProofBoundSolePlayer)
        {
            if (player.TryGetProperty("uuid", out _) || player.TryGetProperty("name", out _))
                throw new InvalidDataException("scenario-proof-bound-player-cannot-declare-synthetic-identity");
            return new(null, null, selector);
        }
        return new(Guid.Parse(RequiredString(player, "uuid")), RequiredString(player, "name"), selector);
    }

    private static QaWorldSeedSelector ParseWorldSeedSelector(JsonElement fixture)
    {
        var hasValue = fixture.TryGetProperty("worldSeed", out _);
        var selector = OptionalString(fixture, "worldSeedSelector");
        if (hasValue && selector is not null)
            throw new InvalidDataException("scenario-world-seed-value-selector-conflict");
        return (hasValue, selector) switch
        {
            (true, null) => QaWorldSeedSelector.Exact,
            (false, "proof_bound") => QaWorldSeedSelector.ProofBound,
            (false, null) => throw new InvalidDataException("scenario-world-seed-policy-missing"),
            _ => throw new InvalidDataException($"scenario-world-seed-selector-unsupported:{selector}")
        };
    }

    private static QaObservedValueSelector ParseObservedSelector(JsonElement fixture, string valueName, string selectorName)
    {
        var hasValue = fixture.TryGetProperty(valueName, out _);
        var selector = OptionalString(fixture, selectorName);
        if (hasValue && selector is not null)
            throw new InvalidDataException($"scenario-{valueName}-value-selector-conflict");
        if (hasValue) return QaObservedValueSelector.Exact;
        return selector switch
        {
            null or "observe_and_pin" => QaObservedValueSelector.ObserveAndPin,
            _ => throw new InvalidDataException($"scenario-{selectorName}-unsupported:{selector}")
        };
    }

    private static JsonElement? Clone(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static string RequiredString(JsonElement parent, string name) =>
        parent.GetProperty(name).GetString() ?? throw new InvalidDataException($"scenario-{name}-missing");

    private static string? OptionalString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value.GetString()
            : null;

    private static IReadOnlyList<string> StringList(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var values)
            ? values.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
            : [];

    private static HashSet<string> Strings(JsonElement parent, string name) =>
        new(StringList(parent, name), StringComparer.Ordinal);

    private static int OptionalInt(JsonElement parent, string name, int fallback) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value.GetInt32()
            : fallback;

    private static long? OptionalLong(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value.GetInt64()
            : null;

    private static string CanonicalSha256(JsonElement root)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, root);
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(value.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidDataException("scenario-json-kind-unsupported");
        }
    }
}
