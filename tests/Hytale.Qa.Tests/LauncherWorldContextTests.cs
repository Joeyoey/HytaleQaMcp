using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class LauncherWorldContextTests
{
    private const string Root = "00000000-0000-4000-8000-000000000001";
    private const string Current = "00000000-0000-4000-8000-000000000002";
    private const string Run = "00000000-0000-4000-8000-000000000003";

    [Fact]
    public void ExactRootWorldRemainsValid()
    {
        using var document = JsonDocument.Parse(
            "{\"worldId\":\"" + Root + "\",\"state\":{}}");
        Assert.True(HytaleScenarioRuntime.LauncherWorldMatches(
            document.RootElement, Root));
    }

    [Fact]
    public void AuthenticatedOwnedInstanceRemainsBoundToLauncherWorld()
    {
        using var document = JsonDocument.Parse($$"""
        {
          "worldId":"{{Current}}",
          "state":{
            "worldName":"instance-a",
            "runAttached":true,
            "runId":"{{Run}}",
            "launcherWorldContext":{
              "schema":"hytale-qa-launcher-world-context-v1",
              "rootWorldId":"{{Root}}",
              "currentWorldId":"{{Current}}",
              "currentWorldName":"instance-a",
              "scope":"owned_instance",
              "playerRunAttached":true,
              "ownerRunId":"{{Run}}"
            }
          }
        }
        """);

        Assert.True(HytaleScenarioRuntime.LauncherWorldMatches(
            document.RootElement, Root));
    }

    [Fact]
    public void UnverifiedOrMismatchedInstanceIsRejected()
    {
        using var document = JsonDocument.Parse($$"""
        {
          "worldId":"{{Current}}",
          "state":{
            "worldName":"instance-a",
            "runAttached":true,
            "runId":"{{Run}}",
            "launcherWorldContext":{
              "schema":"hytale-qa-launcher-world-context-v1",
              "rootWorldId":"{{Root}}",
              "currentWorldId":"{{Current}}",
              "currentWorldName":"instance-a",
              "scope":"unverified",
              "playerRunAttached":true,
              "ownerRunId":"{{Run}}"
            }
          }
        }
        """);

        Assert.False(HytaleScenarioRuntime.LauncherWorldMatches(
            document.RootElement, Root));
    }
}
