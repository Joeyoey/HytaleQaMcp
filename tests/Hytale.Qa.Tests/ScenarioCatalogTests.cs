using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

namespace Hytale.Qa.Tests;

public sealed class ScenarioCatalogTests
{
    [Fact]
    public void PortableExampleScenarioPassesSafetyContracts()
    {
        var paths = ExamplePaths();
        var catalog = new ScenarioCatalog(paths);
        var report = catalog.Validate();

        Assert.True(report.Valid, string.Join(Environment.NewLine,
            report.Issues.Select(issue => $"{issue.Path}: {issue.Code}: {issue.Message}")));
        Assert.Equal(1, report.ScenarioCount);
        var summary = Assert.Single(catalog.List());
        var scenario = QaScenarioLoader.Load(Path.Combine(paths.ProjectRoot, summary.Path));
        Assert.Equal(QaWorldSeedSelector.ProofBound, scenario.Fixture.WorldSeedSelector);
        Assert.Equal(QaPlayerIdentitySelector.ProofBoundSolePlayer, scenario.Fixture.Player.IdentitySelector);
        Assert.Contains(scenario.Steps, step => step.Operation == "trace.mark");
    }

    internal static QaPaths ExamplePaths()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Hytale.Qa.sln"))) cursor = cursor.Parent;
        var root = cursor?.FullName ?? throw new DirectoryNotFoundException("Could not find standalone QA repo root.");
        var project = Path.Combine(root, "examples", "basic-project");
        return new(project, Path.Combine(project, "scripts"),
            Path.Combine(project, "qa-automation", "scenarios"), Path.Combine(project, "run", "control"),
            Path.Combine(project, "client-capability.json"), "server.jar", "HytaleClient.exe")
        {
            ToolRoot = root,
            ServerProfileId = "example-offline",
            SuiteDirectoryOverride = Path.Combine(project, "qa-automation", "suites"),
            EvidenceRootDirectoryOverride = Path.Combine(project, "run", "evidence")
        };
    }
}
