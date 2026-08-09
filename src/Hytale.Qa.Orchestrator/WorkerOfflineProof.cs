using System.Security.Cryptography;
using System.Text.Json;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Orchestrator;

public enum OfflineProofBoundaryKind
{
    IsolatedDockerDedicated,
    LauncherOwnedSingleplayer
}

public interface IWorkerOfflineProofProvider : IDisposable
{
    OfflineProofBoundaryKind Boundary { get; }
    string? SessionId => null;
    Task<OfflineServerProof> GetFreshProofAsync(CancellationToken cancellationToken);
    Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("offline-proof-observation-not-implemented");
    void IDisposable.Dispose() { }
}

public interface IWorkerOfflineProofProviderFactory
{
    IWorkerOfflineProofProvider Create(string sessionId);
    IWorkerOfflineProofProvider CreateDocker(string sessionId) => Create(sessionId);
    IWorkerOfflineProofProvider CreateLauncher(string evidenceFileName) =>
        throw new NotSupportedException("launcher-singleplayer-proof-provider-not-implemented");
}

public sealed class DockerWorkerOfflineProofProviderFactory(
    QaPaths paths,
    OfflineServerController servers,
    IObserverSpoolClientFactory observerClients)
{
    public IWorkerOfflineProofProvider Create(string sessionId)
    {
        var secret = servers.ReadSessionSecret(sessionId);
        return new DockerWorkerOfflineProofProvider(paths, servers,
            observerClients.Create(paths.ObserverSpoolDirectory, secret.SessionNonce), secret);
    }
}

public sealed class DockerWorkerOfflineProofProvider : IWorkerOfflineProofProvider
{
    private readonly QaPaths paths;
    private readonly OfflineServerController servers;
    private readonly IObserverSpoolClient observer;
    private readonly OfflineSessionSecret secret;
    private readonly string serverSha256;
    private readonly long serverLength;
    private readonly DateTime serverLastWriteUtc;
    private readonly SemaphoreSlim proofGate = new(1, 1);
    private bool dockerIdentityVerified;

    public DockerWorkerOfflineProofProvider(QaPaths paths, OfflineServerController servers,
        IObserverSpoolClient observer, OfflineSessionSecret secret)
    {
        this.paths = paths;
        this.servers = servers;
        this.observer = observer;
        this.secret = secret;
        var file = new FileInfo(paths.ServerJarPath);
        if (!file.Exists) throw new FileNotFoundException("Hytale server JAR is missing.", paths.ServerJarPath);
        serverLength = file.Length;
        serverLastWriteUtc = file.LastWriteTimeUtc;
        using var stream = file.OpenRead();
        serverSha256 = Convert.ToHexString(SHA256.HashData(stream));
    }

    public OfflineProofBoundaryKind Boundary => OfflineProofBoundaryKind.IsolatedDockerDedicated;
    public string SessionId => secret.SessionId;

    public async Task<OfflineServerProof> GetFreshProofAsync(CancellationToken cancellationToken)
    {
        await proofGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Docker CLI inspection is intentionally a one-time pre-worker gate. It can take
            // several seconds on Docker Desktop and therefore cannot participate in the worker's
            // two-second liveness heartbeat. HEALTH is an initial authenticated gate; the fresh
            // nonce-bound heartbeat is the live proof and ceases if the plugin boundary disappears.
            if (!dockerIdentityVerified)
            {
                var state = await servers.InspectAsync(secret.SessionId, requireArmed: true, cancellationToken)
                    .ConfigureAwait(false);
                if (!state.Armed || !state.OfflineProven || !state.ObserverReady ||
                    !string.Equals(state.SessionId, secret.SessionId, StringComparison.Ordinal) ||
                    !string.Equals(state.Endpoint, paths.DockerEndpoint, StringComparison.Ordinal) ||
                    !string.Equals(state.ProjectName, paths.DockerProject, StringComparison.Ordinal) ||
                    !string.Equals(state.ContainerName, paths.DockerContainer, StringComparison.Ordinal) ||
                    !ProofBoolean(state.Proof, "loopbackOnly") ||
                    !ProofBoolean(state.Proof, "offlineProven") ||
                    !ProofBoolean(state.Proof, "armed"))
                    throw new InvalidOperationException("Docker offline proof identity or predicates changed.");
            }

            var file = new FileInfo(paths.ServerJarPath);
            if (!file.Exists || file.Length != serverLength || file.LastWriteTimeUtc != serverLastWriteUtc)
                throw new InvalidOperationException("Hytale server JAR identity changed during the QA session.");
            var heartbeat = await observer.VerifyHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            if (!heartbeat.Ready || !heartbeat.Offline || !heartbeat.PacketEvidenceValid)
                throw new InvalidOperationException("Observer no longer attests the offline Docker boundary.");
            if (!dockerIdentityVerified)
            {
                var health = await observer.HealthAsync(cancellationToken).ConfigureAwait(false);
                if (!health.Ready || !health.Offline || !health.PacketEvidenceValid)
                    throw new InvalidOperationException("Observer HEALTH does not attest the offline Docker boundary.");
            }

            dockerIdentityVerified = true;
            return new("offline", true, paths.DockerEndpoint, paths.DockerProject, paths.DockerContainer, paths.DockerNetwork,
                serverSha256, secret.SessionNonce, heartbeat.ObservedAt);
        }
        finally { proofGate.Release(); }
    }

    public Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken) =>
        observer.ObserveSolePlayerAsync(cancellationToken);

    private static bool ProofBoolean(JsonElement proof, string name) =>
        proof.ValueKind == JsonValueKind.Object && proof.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();
}

public sealed class WorkerOfflineProofProviderFactory(
    DockerWorkerOfflineProofProviderFactory docker,
    LauncherSingleplayerProofProviderFactory launcher) : IWorkerOfflineProofProviderFactory
{
    public IWorkerOfflineProofProvider Create(string sessionId) => docker.Create(sessionId);
    public IWorkerOfflineProofProvider CreateDocker(string sessionId) => docker.Create(sessionId);
    public IWorkerOfflineProofProvider CreateLauncher(string evidenceFileName) => launcher.Create(evidenceFileName);
}
