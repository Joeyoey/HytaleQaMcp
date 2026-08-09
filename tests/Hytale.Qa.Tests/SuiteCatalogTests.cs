using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class SuiteCatalogTests
{
    [Fact]
    public void PortableExampleSuitePassesOfflineSerialReferenceContracts()
    {
        var paths = ScenarioCatalogTests.ExamplePaths();
        var catalog = new SuiteCatalog(paths);
        var report = catalog.Validate();

        Assert.True(report.Valid, string.Join(Environment.NewLine,
            report.Issues.Select(issue => $"{issue.Path}: {issue.Code}: {issue.Message}")));
        var release = Assert.Single(catalog.List());
        Assert.Equal("release_gate", release.Purpose);
        Assert.Equal(["smoke.offline-physical-input"], release.ScenarioIds);
    }
}

