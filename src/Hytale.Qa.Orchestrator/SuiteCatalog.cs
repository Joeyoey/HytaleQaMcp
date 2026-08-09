using System.Text.Json;

namespace Hytale.Qa.Orchestrator;

public sealed record QaSuiteSummary(
    string Id,
    string Purpose,
    IReadOnlyList<string> Coverage,
    IReadOnlyList<string> ScenarioIds,
    string Path);

public sealed record QaSuiteValidationIssue(string Path, string Code, string Message);
public sealed record QaSuiteValidationReport(bool Valid, int SuiteCount, int ReferenceCount, IReadOnlyList<QaSuiteValidationIssue> Issues);

public sealed class SuiteCatalog
{
    private static readonly HashSet<string> AllowedPurposes = ["smoke", "regression", "exploratory", "release_gate"];
    private readonly QaPaths paths;

    public SuiteCatalog(QaPaths paths) => this.paths = paths;

    public IReadOnlyList<QaSuiteSummary> List() => Files()
        .Select(Read)
        .OrderBy(suite => suite.Id, StringComparer.Ordinal)
        .ToArray();

    public QaSuiteSummary Get(string id) => List().SingleOrDefault(suite => suite.Id == id)
        ?? throw new KeyNotFoundException($"Unknown QA suite '{id}'.");

    public QaSuiteValidationReport Validate()
    {
        var issues = new List<QaSuiteValidationIssue>();
        var scenarioIds = new HashSet<string>(new ScenarioCatalog(paths).List().Select(value => value.Id), StringComparer.Ordinal);
        var suiteIds = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        var references = 0;
        var releaseGate = false;
        foreach (var path in Files())
        {
            count++;
            var relative = Path.GetRelativePath(paths.ProjectRoot, path);
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                var root = document.RootElement;
                Add(root.GetProperty("schema").GetString() == "hytale-qa-suite/v1", "suite.schema", "Unsupported suite schema.");
                var id = root.GetProperty("id").GetString() ?? "";
                Add(suiteIds.Add(id), "suite.id_duplicate", $"Duplicate suite id '{id}'.");
                var purpose = root.GetProperty("purpose").GetString() ?? "";
                Add(AllowedPurposes.Contains(purpose), "suite.purpose", $"Unsupported suite purpose '{purpose}'.");
                releaseGate |= purpose == "release_gate";

                var execution = root.GetProperty("execution");
                Add(execution.GetProperty("order").GetString() == "listed", "suite.order", "Suite order must be listed and deterministic.");
                Add(execution.GetProperty("maximumParallelClients").GetInt32() == 1, "suite.client_count", "Suites must be serial with one client.");
                Add(execution.GetProperty("freshSessionPerScenario").GetBoolean(), "suite.fresh_session", "Every scenario requires a fresh session.");
                Add(execution.GetProperty("requireOfflineProof").GetBoolean(), "suite.offline", "Every suite requires offline proof.");

                var criteria = root.GetProperty("releaseCriteria");
                Add(criteria.GetProperty("requireAllPass").GetBoolean(), "suite.all_pass", "All scenarios must pass.");
                Add(!criteria.GetProperty("allowUntested").GetBoolean(), "suite.untested", "Untested results may not pass a suite.");
                Add(!criteria.GetProperty("allowWhiteBoxForPlayerFacing").GetBoolean(), "suite.white_box", "White-box evidence cannot prove player-facing behavior.");

                var localIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scenario in root.GetProperty("scenarios").EnumerateArray())
                {
                    references++;
                    var scenarioId = scenario.GetString() ?? "";
                    Add(localIds.Add(scenarioId), "suite.reference_duplicate", $"Scenario '{scenarioId}' is repeated.");
                    Add(scenarioIds.Contains(scenarioId), "suite.reference_missing", $"Scenario '{scenarioId}' does not exist.");
                }

                void Add(bool condition, string code, string message)
                {
                    if (!condition) issues.Add(new(relative, code, message));
                }
            }
            catch (Exception failure) when (failure is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                issues.Add(new(relative, "suite.parse", failure.Message));
            }
        }
        if (count == 0) issues.Add(new(paths.SuiteDirectory, "suite.none", "No suite manifests were found."));
        if (!releaseGate) issues.Add(new(paths.SuiteDirectory, "suite.release_missing", "No release_gate suite exists."));
        return new(issues.Count == 0, count, references, issues);
    }

    private IEnumerable<string> Files() => Directory.Exists(paths.SuiteDirectory)
        ? Directory.EnumerateFiles(paths.SuiteDirectory, "*.json", SearchOption.TopDirectoryOnly)
        : [];

    private QaSuiteSummary Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        return new(
            root.GetProperty("id").GetString() ?? "",
            root.GetProperty("purpose").GetString() ?? "",
            root.GetProperty("coverage").EnumerateArray().Select(value => value.GetString() ?? "").ToArray(),
            root.GetProperty("scenarios").EnumerateArray().Select(value => value.GetString() ?? "").ToArray(),
            Path.GetRelativePath(paths.ProjectRoot, path));
    }
}
