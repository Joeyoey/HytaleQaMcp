using Hytale.Qa.Contracts;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class SafetyPolicyValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1:5542")]
    [InlineData("udp://127.0.0.1:5542")]
    [InlineData("[::1]:5542")]
    public void AcceptsExplicitLoopback(string endpoint)
    {
        Assert.True(SafetyPolicyValidator.Validate(Policy(endpoint)).Safe);
    }

    [Theory]
    [InlineData("localhost:5542")]
    [InlineData("192.168.1.10:5542")]
    [InlineData("8.8.8.8:5542")]
    [InlineData("127.0.0.1:0")]
    public void RejectsNonExplicitOrInvalidEndpoint(string endpoint)
    {
        Assert.False(SafetyPolicyValidator.Validate(Policy(endpoint)).Safe);
    }

    [Fact]
    public void RejectsMissingOfflineAttestation()
    {
        var policy = Policy("127.0.0.1:5542") with { OfflineAttested = false };
        Assert.Equal("server.not_offline", SafetyPolicyValidator.Validate(policy).Code);
    }

    [Fact]
    public void OfflineProofMustMatchEndpointNonceAndModeCapabilities()
    {
        var policy = Policy("127.0.0.1:5542");
        var proof = new OfflineServerProof("offline", true, policy.ServerEndpoint, "hytale-qa", "hytale-qa-server", "hytale-qa-net",
            new string('B', 64), policy.Nonce, DateTimeOffset.UtcNow);
        var capabilities = new EvidenceCapabilities(true, true, true, false, false, false);
        Assert.True(SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities).Safe);
        Assert.Equal("proof.endpoint", SafetyPolicyValidator.ValidateOfflineProof(policy, proof with { ServerEndpoint = "127.0.0.1:9999" }, capabilities).Code);
        Assert.Equal("proof.nonce", SafetyPolicyValidator.ValidateOfflineProof(policy, proof with { ObserverNonce = "wrong" }, capabilities).Code);
        Assert.Equal("capability.mode", SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities with { DirectDamage = true }).Code);
    }

    [Fact]
    public void OfflineProofMustBeFresh()
    {
        var policy = Policy("127.0.0.1:5542");
        var proof = new OfflineServerProof("offline", true, policy.ServerEndpoint, "hytale-qa", "hytale-qa-server", "hytale-qa-net",
            new string('B', 64), policy.Nonce, DateTimeOffset.UtcNow.AddSeconds(-11));
        var capabilities = new EvidenceCapabilities(true, true, true, false, false, false);

        Assert.Equal("proof.stale", SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities).Code);
    }

    [Fact]
    public void BlackBoxCannotArmCombatReflex()
    {
        var policy = Policy("127.0.0.1:5542") with { AssistanceMode = AssistanceMode.BlackBox };
        var proof = new OfflineServerProof("offline", true, policy.ServerEndpoint, "hytale-qa", "hytale-qa-server", "hytale-qa-net",
            new string('B', 64), policy.Nonce, DateTimeOffset.UtcNow);
        var capabilities = new EvidenceCapabilities(true, true, true, false, false, false);

        Assert.Equal("capability.black_box", SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities).Code);
    }

    [Fact]
    public void LauncherProofIsStrictlyDistinctAndMustMatchExactClientLease()
    {
        var policy = Policy("127.0.0.1:7788");
        var started = DateTimeOffset.UtcNow.AddSeconds(-5);
        var client = new ProcessIdentity(22, started, @"C:\qa\HytaleClient.exe", new string('C', 64), 42);
        var evidence = new LauncherSingleplayerEvidence(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            new string('1', 64), new string('2', 64),
            new(11, 1, started.AddSeconds(-1), @"C:\qa\HytaleLauncher.exe", new string('A', 64)),
            new(22, 11, started, client.ExecutablePath, client.Sha256),
            new(33, 22, started.AddSeconds(1), @"C:\qa\java.exe", new string('D', 64)),
            new string('E', 64), new string('F', 64), new string('9', 64), true, true, true);
        var proof = new OfflineServerProof("offline", true, policy.ServerEndpoint, "", "", "",
            evidence.ServerArtifactSha256, policy.Nonce, DateTimeOffset.UtcNow,
            OfflineProofKind.LauncherOwnedSingleplayer, evidence);
        var capabilities = new EvidenceCapabilities(true, true, true, false, false, false);

        Assert.True(SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities, client).Safe);
        Assert.Equal("proof.client_lease", SafetyPolicyValidator.ValidateOfflineProof(policy, proof, capabilities,
            client with { ProcessId = 99 }).Code);
        Assert.Equal("proof.boundary_mixed", SafetyPolicyValidator.ValidateOfflineProof(policy,
            proof with { DockerProject = "pretend-docker" }, capabilities, client).Code);
        Assert.Equal("proof.launcher_tree", SafetyPolicyValidator.ValidateOfflineProof(policy,
            proof with { Launcher = evidence with { Server = evidence.Server with { ParentProcessId = 11 } } },
            capabilities, client).Code);
    }

    private static SessionSafetyPolicy Policy(string endpoint) => new(
        "session", "nonce", AssistanceMode.GuidedPhysical, true, endpoint, "HytaleClient.exe",
        [@"C:\qa\HytaleClient.exe"], [new string('A', 64)]);
}
