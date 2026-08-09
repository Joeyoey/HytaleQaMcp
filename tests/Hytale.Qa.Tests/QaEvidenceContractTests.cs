using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class QaEvidenceContractTests
{
    [Fact]
    public void VersionedProjectionSupportsIsoTimestampsAndHashArray()
    {
        using var document = JsonDocument.Parse("""
        {
          "supportedEventTypes":["native.gear.delivered"],
          "supportedHashSubjects":["equipment_operation_wal"],
          "events":[{"event":"native.gear.delivered","occurredAt":"2026-08-08T12:34:56Z"}],
          "hashes":[{"subject":"equipment_operation_wal","hashValid":true,"openOperations":0}]
        }
        """);
        var evidence = document.RootElement;

        Assert.True(HytaleScenarioRuntime.EvidenceSupports(evidence, "supportedEventTypes", "native.gear.delivered"));
        Assert.False(HytaleScenarioRuntime.EvidenceSupports(evidence, "supportedEventTypes", "run.reward.committed"));
        Assert.Equal(DateTimeOffset.Parse("2026-08-08T12:34:56Z").ToUnixTimeMilliseconds(),
            HytaleScenarioRuntime.EventTimestamp(evidence.GetProperty("events")[0]));
        Assert.True(HytaleScenarioRuntime.TryHashEvidence(evidence, "equipment_operation_wal", out var hash));
        Assert.True(hash.GetProperty("hashValid").GetBoolean());
        Assert.False(HytaleScenarioRuntime.TryHashEvidence(evidence, "complete_session", out _));
    }
}
