using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class SemanticUiEvidenceTests
{
    [Fact]
    public void AuthenticatedPageIdMatchesExpectedInspectionPage()
    {
        using var document = JsonDocument.Parse("""
        {"semanticUi":{"available":true,"pageId":"confirmation_page","revision":1,"nodes":[]}}
        """);

        Assert.True(HytaleScenarioRuntime.SemanticUiPageMatches(
            document.RootElement, "confirmation_page"));
        Assert.False(HytaleScenarioRuntime.SemanticUiPageMatches(
            document.RootElement, "inventory_page"));
    }

    [Fact]
    public void ClosingAuthenticatedPageCountsAsSemanticTransition()
    {
        using var before = JsonDocument.Parse("""
        {"available":true,"pageId":"confirmation_page","revision":7,"nodes":[]}
        """);
        using var after = JsonDocument.Parse("""{"runAttached":true}""");

        Assert.True(HytaleScenarioRuntime.SemanticUiChanged(
            before.RootElement, after.RootElement, out _));
    }

    [Fact]
    public void UnchangedPageDoesNotPassFromWorldTickNoise()
    {
        using var before = JsonDocument.Parse("""
        {"available":true,"pageId":"confirmation_page","revision":7,"nodes":[]}
        """);
        using var after = JsonDocument.Parse("""
        {"worldTick":99,"semanticUi":{"available":true,"pageId":"confirmation_page","revision":7,"nodes":[]}}
        """);

        Assert.False(HytaleScenarioRuntime.SemanticUiChanged(
            before.RootElement, after.RootElement, out _));
    }
}
