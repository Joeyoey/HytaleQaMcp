using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class ObservationRetryPolicyTests
{
    [Fact]
    public void OnlyTransientWorldTransferGapIsRetryable()
    {
        Assert.True(HytaleScenarioRuntime.IsTransientObservationFailure(
            new ObserverSpoolException(
                "observer-server-snapshot-unavailable", "transient")));
        Assert.False(HytaleScenarioRuntime.IsTransientObservationFailure(
            new ObserverSpoolException("offline-proof-lost", "unsafe")));
        Assert.False(HytaleScenarioRuntime.IsTransientObservationFailure(
            new ObserverSpoolException("observer-evidence-invalid", "unsafe")));
    }
}
