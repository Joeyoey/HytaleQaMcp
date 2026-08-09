using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class QaCapabilityAuditorTests
{
    [Fact]
    public void PortableSmokeIsFullyBackedAndReportsPerOperationSupport()
    {
        var toolRoot = FindToolRoot();
        var paths = QaPaths.FromProfile(Path.Combine(toolRoot, "examples", "basic-project", ".hytale-qa", "profile.json"));

        var audit = new QaCapabilityAuditor(paths, new ScenarioCatalog(paths)).Audit();

        Assert.Equal(1, audit.ScenarioCount);
        Assert.Equal(1, audit.FullyAdapterBackedScenarioCount);
        Assert.All(audit.Operations, operation => Assert.Equal(operation.StepCount, operation.SupportedStepCount));
        Assert.Empty(audit.MissingObserverContracts);
    }

    private static string FindToolRoot()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Hytale.Qa.sln")))
            cursor = cursor.Parent;
        return cursor?.FullName ?? throw new DirectoryNotFoundException("Hytale QA tool root not found.");
    }
}
