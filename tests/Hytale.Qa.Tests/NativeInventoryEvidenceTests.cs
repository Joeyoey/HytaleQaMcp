using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class NativeInventoryEvidenceTests
{
    [Fact]
    public void GenericSemanticItemsAndLatestRewardResolveWithoutChangingSignedStackAccounting()
    {
        using var document = JsonDocument.Parse("""
        {
          "available": true,
          "signedStackCount": 1,
          "items": [{"semanticId":"signed-item","signatureValid":true,"active":true}],
          "semanticSelectors": {
            "schema":"hytale-qa-native-inventory-selectors-v1",
            "roleItems":[{
              "semanticId":"role_item","selectorId":"semantic.role.alpha","variant":"Alpha",
              "nonConsumable":true,"ownerBound":true,"signatureValid":true
            }],
            "latestSignedReward": {
              "semanticId":"latest_signed_reward","status":"PRESENT",
              "itemId":"reward-1","signatureValid":true
            }
          }
        }
        """);

        var candidates = HytaleScenarioRuntime.InventoryCandidates(document.RootElement);

        Assert.Equal(3, candidates.Count);
        Assert.Single(candidates, item =>
            HytaleScenarioRuntime.InventoryItemMatches(item, "semantic.role.alpha"));
        Assert.Single(candidates, item =>
            HytaleScenarioRuntime.InventoryItemMatches(item, "latest_signed_reward"));
    }

    [Fact]
    public void AssertStateDerivesCountsFromNativeInventoryObjectItems()
    {
        using var document = JsonDocument.Parse("""
        {"nativeInventory":{"signedStackCount":2,"items":[
          {"signatureValid":true,"active":true},
          {"signatureValid":true,"active":false}
        ]}}
        """);

        Assert.True(HytaleScenarioRuntime.TryResolveAssertValue(document.RootElement, "signedItemCount", out var count));
        Assert.Equal(2, count.GetInt32());
        Assert.True(HytaleScenarioRuntime.TryResolveAssertValue(document.RootElement, "provenanceValid", out var provenance));
        Assert.True(provenance.GetBoolean());
        Assert.True(HytaleScenarioRuntime.TryResolveAssertValue(document.RootElement, "activeItemCount", out var active));
        Assert.Equal(1, active.GetInt32());
    }
}
