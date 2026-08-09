using System.Text.Json;

namespace Hytale.Qa.Orchestrator;

public sealed record QaScenarioSummary(string Id, string Mode, IReadOnlyList<string> Tags, int StepCount, string Path);
public sealed record QaScenarioValidationIssue(string Path, string Code, string Message);
public sealed record QaScenarioValidationReport(bool Valid, int ScenarioCount, IReadOnlyList<QaScenarioValidationIssue> Issues);

public sealed class ScenarioCatalog
{
    private static readonly HashSet<string> AllowedModes = ["black_box", "guided_physical", "white_box"];
    private static readonly HashSet<string> ForbiddenOperations = ["command.execute", "input.raw", "packet.inject"];
    private readonly QaPaths paths;

    public ScenarioCatalog(QaPaths paths) => this.paths = paths;

    public IReadOnlyList<QaScenarioSummary> List()
    {
        var scenarios = new List<QaScenarioSummary>();
        foreach (var path in Files())
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            scenarios.Add(new(
                root.GetProperty("id").GetString() ?? "",
                root.GetProperty("mode").GetString() ?? "",
                root.TryGetProperty("tags", out var tags)
                    ? tags.EnumerateArray().Select(value => value.GetString() ?? "").ToArray()
                    : [],
                root.GetProperty("steps").GetArrayLength(),
                Path.GetRelativePath(paths.ProjectRoot, path)));
        }
        return scenarios.OrderBy(value => value.Id, StringComparer.Ordinal).ToArray();
    }

    public QaScenarioValidationReport Validate()
    {
        var issues = new List<QaScenarioValidationIssue>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        foreach (var path in Files())
        {
            count++;
            var relative = Path.GetRelativePath(paths.ProjectRoot, path);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Validate(document.RootElement, relative, ids, issues);
            }
            catch (Exception failure) when (failure is JsonException or InvalidOperationException)
            {
                issues.Add(new(relative, "scenario.parse", failure.Message));
            }
        }
        if (count == 0) issues.Add(new(paths.ScenarioDirectory, "scenario.none", "No scenario files were found."));
        return new(issues.Count == 0, count, issues);
    }

    private IEnumerable<string> Files() => Directory.Exists(paths.ScenarioDirectory)
        ? Directory.EnumerateFiles(paths.ScenarioDirectory, "*.json", SearchOption.AllDirectories)
        : [];

    private void Validate(JsonElement root, string path, HashSet<string> ids, List<QaScenarioValidationIssue> issues)
    {
        var schema = root.GetProperty("schema").GetString();
        Add(schema == "hytale-qa/v1", "scenario.schema", "Unsupported scenario schema.");
        var id = root.GetProperty("id").GetString() ?? "";
        Add(ids.Add(id), "scenario.id_duplicate", $"Duplicate scenario id '{id}'.");
        var mode = root.GetProperty("mode").GetString() ?? "";
        Add(AllowedModes.Contains(mode), "scenario.mode", $"Unsupported mode '{mode}'.");
        var fixture = root.GetProperty("fixture");
        Add(fixture.GetProperty("serverProfile").GetString() == paths.ServerProfileId, "scenario.profile",
            $"Scenario is not pinned to configured offline profile '{paths.ServerProfileId}'.");
        var hasWorldSeed = fixture.TryGetProperty("worldSeed", out _);
        var worldSeedSelector = fixture.TryGetProperty("worldSeedSelector", out var worldSeedSelectorValue)
            ? worldSeedSelectorValue.GetString()
            : null;
        Add(hasWorldSeed ^ worldSeedSelector is not null, "scenario.world_seed_policy",
            "Declare either numeric worldSeed or worldSeedSelector, but not both.");
        if (!hasWorldSeed)
            Add(worldSeedSelector == "proof_bound", "scenario.world_seed_selector",
                $"Unsupported world seed selector '{worldSeedSelector}'.");
        ValidateObservedPolicy("runSeed", "runSeedSelector");
        ValidateObservedPolicy("snapshot", "snapshotSelector");
        ValidateObservedPolicy("loadout", "loadoutSelector");
        var player = fixture.GetProperty("player");
        var identitySelector = player.TryGetProperty("identitySelector", out var selectorValue)
            ? selectorValue.GetString()
            : "exact_fixture";
        Add(identitySelector is "exact_fixture" or "proof_bound_sole_player", "scenario.player_selector",
            $"Unsupported player identity selector '{identitySelector}'.");
        if (identitySelector == "proof_bound_sole_player")
            Add(!player.TryGetProperty("uuid", out _) && !player.TryGetProperty("name", out _),
                "scenario.player_synthetic_identity",
                "Proof-bound sole-player selection cannot declare a synthetic fixture UUID or name.");
        else
            Add(player.TryGetProperty("uuid", out _) && player.TryGetProperty("name", out _),
                "scenario.player_exact_identity", "Exact fixture selection requires UUID and name.");
        var safety = root.GetProperty("safety");
        Add(safety.GetProperty("offlineRequired").GetBoolean(), "scenario.offline", "Offline mode is mandatory.");
        Add(safety.GetProperty("loopbackOnly").GetBoolean(), "scenario.loopback", "Loopback-only mode is mandatory.");
        Add(safety.GetProperty("maximumClientProcesses").GetInt32() == 1, "scenario.client_count", "Exactly one client must be required.");
        var capabilities = root.TryGetProperty("capabilities", out var capabilityElement)
            ? capabilityElement.EnumerateArray().Select(value => value.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : [];
        if (mode != "white_box")
            Add(!capabilities.Overlaps(["teleport", "direct_damage", "state_setup", "fault_injection"]),
                "scenario.assistance", "Mutating assistance is permitted only in white-box mode.");
        if (mode == "black_box")
            Add(!capabilities.Contains("combat_reflex") && !capabilities.Contains("observer_guidance"),
                "scenario.black_box", "Black-box scenarios cannot use guidance or combat reflex.");

        var steps = root.GetProperty("steps").EnumerateArray().ToArray();
        Add(steps.Length >= 2, "scenario.steps", "A scenario requires start and stop steps.");
        if (steps.Length > 0)
        {
            Add(steps[0].GetProperty("operation").GetString() == "session.start", "scenario.first_step", "First step must be session.start.");
            Add(steps[^1].GetProperty("operation").GetString() == "session.stop", "scenario.last_step", "Last step must be session.stop.");
        }
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString() ?? "";
            var operation = step.GetProperty("operation").GetString() ?? "";
            Add(stepIds.Add(stepId), "scenario.step_id", $"Duplicate step id '{stepId}'.");
            Add(!ForbiddenOperations.Contains(operation), "scenario.unsafe_operation", $"Unsafe operation '{operation}'.");
            if (operation == "combat.clear")
                Add(mode == "guided_physical" && capabilities.Contains("combat_reflex"),
                    "scenario.combat_mode", "combat.clear requires guided_physical combat_reflex capability.");
        }

        void ValidateObservedPolicy(string valueName, string selectorName)
        {
            var hasValue = fixture.TryGetProperty(valueName, out _);
            var hasSelector = fixture.TryGetProperty(selectorName, out var selector);
            Add(!(hasValue && hasSelector), $"scenario.{valueName}_policy",
                $"Declare {valueName} or {selectorName}, not both.");
            if (hasSelector)
                Add(selector.GetString() == "observe_and_pin", $"scenario.{selectorName}",
                    $"Unsupported {selectorName} '{selector.GetString()}'.");
        }

        void Add(bool condition, string code, string message)
        {
            if (!condition) issues.Add(new(path, code, message));
        }
    }
}
