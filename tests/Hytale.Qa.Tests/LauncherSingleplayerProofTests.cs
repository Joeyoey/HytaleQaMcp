using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hytale.Qa.Contracts;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class LauncherSingleplayerProofTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-launcher-proof-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AuthenticatedExactWorldProcessAndFilePinsProduceLauncherProof()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var observer = new ReadyLauncherObserver();
        using var provider = new LauncherSingleplayerProofProvider(record, nonce,
            new FixedProcessInspector(record.Launcher, record.Client, record.Server), observer);

        var proof = await provider.GetFreshProofAsync(CancellationToken.None);

        Assert.Equal(OfflineProofKind.LauncherOwnedSingleplayer, proof.Kind);
        Assert.Equal(OfflineProofBoundaryKind.LauncherOwnedSingleplayer, provider.Boundary);
        Assert.Empty(proof.DockerProject);
        Assert.Equal(record.WorldId, proof.Launcher!.WorldId);
        Assert.True(proof.Launcher.ProcessTreeVerified);
        Assert.True(proof.Launcher.LoopbackOnlyVerified);
        Assert.True(proof.Launcher.ObserverBoundaryVerified);
        Assert.Equal(1, observer.HealthCalls);

        _ = await provider.GetFreshProofAsync(CancellationToken.None);
        Assert.Equal(1, observer.HealthCalls);
        Assert.Equal(2, observer.HeartbeatCalls);
    }

    [Fact]
    public async Task AuthenticatedDeadLauncherParentRemainsBoundToLiveClient()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        using var provider = new LauncherSingleplayerProofProvider(record, nonce,
            new MissingLauncherInspector(record.Launcher.ProcessId, record.Client, record.Server),
            new ReadyLauncherObserver());

        var proof = await provider.GetFreshProofAsync(CancellationToken.None);

        Assert.True(proof.Launcher!.ProcessTreeVerified);
        Assert.Equal(record.Launcher.ProcessId, record.Client.ParentProcessId);
    }

    [Fact]
    public void ReusedLauncherPidWithDifferentIdentityFailsClosed()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var reused = record.Launcher with { Sha256 = new string('F', 64) };

        Assert.Throws<InvalidOperationException>(() =>
            new LauncherSingleplayerProofProvider(record, nonce,
                new FixedProcessInspector(reused, record.Client, record.Server),
                new ReadyLauncherObserver()));
    }

    [Fact]
    public void RejectsTamperedLaunchEvidenceAndProcessTree()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var processes = new FixedProcessInspector(record.Launcher, record.Client, record.Server);
        Assert.Throws<InvalidDataException>(() => new LauncherSingleplayerProofProvider(
            record with { WorldId = Guid.NewGuid().ToString("D") }, nonce, processes, new ReadyLauncherObserver()));

        var wrongServer = record.Server with { ParentProcessId = record.Launcher.ProcessId };
        var changed = Authenticate(record with { Server = wrongServer }, nonce);
        Assert.Throws<InvalidDataException>(() => new LauncherSingleplayerProofProvider(changed, nonce,
            new FixedProcessInspector(changed.Launcher, changed.Client, changed.Server), new ReadyLauncherObserver()));
    }

    [Fact]
    public async Task PinnedArtifactHandlePreventsLengthChangingMutationDuringSession()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var observer = new ReadyLauncherObserver();
        using var provider = new LauncherSingleplayerProofProvider(record, nonce,
            new FixedProcessInspector(record.Launcher, record.Client, record.Server), observer);
        Assert.Throws<IOException>(() => File.AppendAllText(record.AssetsPath, "changed"));

        _ = await provider.GetFreshProofAsync(CancellationToken.None);
        Assert.Equal(1, observer.HeartbeatCalls);
    }

    [Fact]
    public async Task PinnedArtifactHandlePreventsSameMetadataReplacementDuringSession()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var observer = new ReadyLauncherObserver();
        using var provider = new LauncherSingleplayerProofProvider(record, nonce,
            new FixedProcessInspector(record.Launcher, record.Client, record.Server), observer);
        Assert.Throws<IOException>(() => File.WriteAllText(record.AssetsPath, "mutate"));

        _ = await provider.GetFreshProofAsync(CancellationToken.None);
        Assert.Equal(1, observer.HeartbeatCalls);
    }

    [Fact]
    public void EvidenceSealAuthenticatesPurposeIdentityAndArtifactHash()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var paths = new QaPaths(root, root, root, Path.Combine(root, "docker"), root, root, root);
        Directory.CreateDirectory(paths.LauncherProofDirectory);
        var fileName = record.SessionId + ".json";
        File.WriteAllText(Path.Combine(paths.LauncherProofDirectory, fileName),
            JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var factory = new LauncherSingleplayerProofProviderFactory(paths, null!, null!);
        var sha = new string('D', 64);

        var seal = factory.Seal(fileName, "artifact-manifest", sha);

        factory.VerifySeal(seal, "artifact-manifest", sha);
        Assert.Throws<InvalidDataException>(() => factory.VerifySeal(seal, "run-result", sha));
        Assert.Throws<InvalidDataException>(() => factory.VerifySeal(seal with { Mac = new string('0', 64) },
            "artifact-manifest", sha));
    }

    [Fact]
    public void EvidenceSealAcceptsLegacyUtf8BomProofRecord()
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var record = Record(nonce);
        var paths = new QaPaths(root, root, root, Path.Combine(root, "docker"), root, root, root);
        Directory.CreateDirectory(paths.LauncherProofDirectory);
        var fileName = record.SessionId + ".json";
        var node = JsonNode.Parse(JsonSerializer.Serialize(record,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        node["launcher"]!.AsObject()["schema"] = "hytale-qa-launcher-bootstrap-evidence-v1";
        node["launcher"]!.AsObject()["mac"] = new string('0', 64);
        File.WriteAllText(Path.Combine(paths.LauncherProofDirectory, fileName), node.ToJsonString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var factory = new LauncherSingleplayerProofProviderFactory(paths, null!, null!);
        var sha = new string('D', 64);

        var seal = factory.Seal(fileName, "artifact-manifest", sha);

        factory.VerifySeal(seal, "artifact-manifest", sha);
    }

    private LauncherSingleplayerProofRecord Record(string nonce)
    {
        Directory.CreateDirectory(root);
        var world = Path.Combine(root, "world");
        var spool = Path.Combine(root, "spool");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(spool);
        var manifest = Write("world", "manifest.json", "world-manifest");
        var serverArtifact = Write(root, "HytaleServer.jar", "server");
        var assets = Write(root, "Assets.zip", "assets");
        var launcherLog = Write(root, "launcher.log.snapshot", "launcher-log");
        var clientLog = Write(root, "client.log.snapshot", "client-log");
        var worldServerLog = Write(root, "world-server.log.snapshot", "world-server-log");
        var at = DateTimeOffset.UtcNow.AddSeconds(-1);
        var record = new LauncherSingleplayerProofRecord(
            LauncherSingleplayerProofProvider.Schema, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            LauncherProofAuthentication.ProtectNonce(nonce), "127.0.0.1:7788", Guid.NewGuid().ToString("D"),
            world, manifest, Hash(manifest),
            new(101, 1, at.AddSeconds(-2), Path.Combine(root, "launcher.exe"), new string('A', 64)),
            new(202, 101, at.AddSeconds(-1), Path.Combine(root, "HytaleClient.exe"), new string('B', 64)),
            new(303, 202, at, Path.Combine(root, "java.exe"), new string('C', 64)),
            serverArtifact, Hash(serverArtifact), assets, Hash(assets), spool,
            launcherLog, Hash(launcherLog), clientLog, Hash(clientLog), worldServerLog, Hash(worldServerLog),
            at, new string('0', 64), new string('0', 64));
        return Authenticate(record, nonce);
    }

    private static LauncherSingleplayerProofRecord Authenticate(LauncherSingleplayerProofRecord record, string nonce) =>
        record with
        {
            LaunchEvidenceSha256 = LauncherProofAuthentication.ComputeSha256(record),
            LaunchEvidenceMac = LauncherProofAuthentication.ComputeMac(record, nonce)
        };

    private string Write(string directory, string fileName, string contents)
    {
        var path = Path.Combine(directory == "world" ? Path.Combine(root, directory) : directory, fileName);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class FixedProcessInspector(params PinnedProcessEvidence[] pins) : ILauncherProcessInspector
    {
        public PinnedProcessEvidence Inspect(int processId) => pins.Single(pin => pin.ProcessId == processId);
    }

    private sealed class MissingLauncherInspector(int launcherPid, params PinnedProcessEvidence[] pins)
        : ILauncherProcessInspector
    {
        public PinnedProcessEvidence Inspect(int processId) => processId == launcherPid
            ? throw new ArgumentException("Process is not running.", nameof(processId))
            : pins.Single(pin => pin.ProcessId == processId);
    }

    private sealed class ReadyLauncherObserver : IObserverSpoolClient
    {
        public int HeartbeatCalls { get; private set; }
        public int HealthCalls { get; private set; }
        public Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken)
        {
            HeartbeatCalls++;
            return Task.FromResult(new ObserverHeartbeat(true, true, "OFFLINE", "hash", true, 1, DateTimeOffset.UtcNow));
        }
        public Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken)
        {
            HealthCalls++;
            return Task.FromResult(new ObserverHealth(true, true, "OFFLINE",
                LauncherSingleplayerProofProvider.ObserverBoundary, "hash", true));
        }
        public Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
