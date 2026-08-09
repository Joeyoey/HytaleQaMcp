using System.Security.Cryptography;
using System.Text.Json;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Orchestrator;

public enum QaSessionPhase
{
    Idle,
    StartingOfflineServer,
    OfflineServerArmed,
    BlockedPrerequisite,
    ClientLeaseReady,
    Armed,
    Faulted,
    Stopping
}

public sealed record QaSessionStatus(
    string? SessionId,
    QaSessionPhase Phase,
    bool OfflineServerArmed,
    bool ClientInputArmed,
    string? Endpoint,
    string? BlockerCode,
    string Message,
    DateTimeOffset UpdatedAtUtc,
    string? SessionNonceSha256 = null);

public sealed class QaSessionOrchestrator
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly QaPaths paths;
    private readonly OfflineServerController servers;
    private readonly IObserverSpoolClientFactory observerClients;
    private readonly IWorkerControlService? workerControl;
    private IObserverSpoolClient? observer;
    private QaSessionStatus status = new(null, QaSessionPhase.Idle, false, false, null, null, "No QA session exists.", DateTimeOffset.UtcNow);

    public QaSessionOrchestrator(
        QaPaths paths,
        OfflineServerController servers,
        IObserverSpoolClientFactory? observerClients = null,
        IWorkerControlService? workerControl = null)
    {
        this.paths = paths;
        this.servers = servers;
        this.observerClients = observerClients ?? new ObserverSpoolClientFactory();
        this.workerControl = workerControl;
    }

    public QaSessionStatus Status => status;
    public WorkerControlState? WorkerState => workerControl?.State;

    public async Task<QaSessionStatus> AttachClientAsync(int processId, bool combatReflex,
        CancellationToken cancellationToken)
    {
        if (workerControl is null) throw new InvalidOperationException("Worker control is not configured.");
        var refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed.OfflineServerArmed || refreshed.Phase == QaSessionPhase.BlockedPrerequisite)
            throw new InvalidOperationException(refreshed.BlockerCode ?? "Offline client boundary is not compatible.");
        var sessionId = refreshed.SessionId ?? throw new InvalidOperationException("No QA session exists.");
        var capabilities = new EvidenceCapabilities(true, true, combatReflex, false, false, false);
        var worker = await workerControl.AttachAsync(processId, sessionId, AssistanceMode.GuidedPhysical,
            capabilities, cancellationToken).ConfigureAwait(false);
        if (worker.Phase != WorkerControlPhase.Armed)
            throw new InvalidOperationException("Worker did not arm.");
        status = status with
        {
            Phase = QaSessionPhase.Armed,
            ClientInputArmed = true,
            BlockerCode = null,
            Message = "Exactly one allowlisted client is attached to the offline-proof heartbeat.",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        return status;
    }

    public async Task<QaSessionStatus> DetachClientAsync(CancellationToken cancellationToken)
    {
        if (workerControl is not null) await workerControl.DetachAsync(cancellationToken).ConfigureAwait(false);
        if (status.SessionId is not null)
            status = status with
            {
                Phase = status.OfflineServerArmed ? QaSessionPhase.OfflineServerArmed : QaSessionPhase.Faulted,
                ClientInputArmed = false,
                Message = "Client worker detached; offline server state is unchanged.",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        return status;
    }

    public async Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("Player id cannot be empty.", nameof(playerId));
        var refreshed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshed.OfflineServerArmed || observer is null)
            throw new ObserverSpoolException("offline-proof-lost", "Observation is unavailable without a live offline proof.");
        return await observer.ObserveAsync(playerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QaSessionStatus> StartAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (status.Phase is not QaSessionPhase.Idle and not QaSessionPhase.Faulted)
                throw new InvalidOperationException("A QA session is already active or starting.");
            var sessionId = Guid.NewGuid().ToString("D");
            status = new(sessionId, QaSessionPhase.StartingOfflineServer, false, false, null, null,
                "Starting the isolated offline QA server.", DateTimeOffset.UtcNow);
            try
            {
                var server = await servers.StartAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (!server.Armed || !server.OfflineProven || !server.ObserverReady)
                    throw new InvalidOperationException("The isolated server started without complete offline proof.");
                var secret = servers.ReadSessionSecret(sessionId);
                observer = observerClients.Create(paths.ObserverSpoolDirectory, secret.SessionNonce);
                await observer.VerifyHeartbeatAsync(cancellationToken).ConfigureAwait(false);
                await observer.HealthAsync(cancellationToken).ConfigureAwait(false);
                status = new(sessionId, QaSessionPhase.OfflineServerArmed, true, false, server.Endpoint, null,
                    "Offline server proof accepted; evaluating fixture client capability.", DateTimeOffset.UtcNow,
                    Sha256(secret.SessionNonce));

                var capability = ReadClientCapability();
                if (!capability.DedicatedOfflineCompatible)
                {
                    status = status with
                    {
                        Phase = QaSessionPhase.BlockedPrerequisite,
                        BlockerCode = capability.BlockerCode,
                        Message = "The installed retail client cannot join a dedicated offline server. Input remains disarmed.",
                        UpdatedAtUtc = DateTimeOffset.UtcNow
                    };
                }
                return status;
            }
            catch (Exception failure)
            {
                observer = null;
                string? cleanupFailure = null;
                try
                {
                    await servers.StopAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception stopFailure)
                {
                    cleanupFailure = stopFailure.Message;
                }
                status = status with
                {
                    Phase = QaSessionPhase.Faulted,
                    BlockerCode = "offline-session-start-failed",
                    Message = cleanupFailure is null
                        ? failure.Message
                        : $"{failure.Message} Cleanup also failed: {cleanupFailure}",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<QaSessionStatus> RefreshAsync(CancellationToken cancellationToken)
    {
        var sessionId = status.SessionId ?? throw new InvalidOperationException("No QA session exists.");
        try
        {
            var server = await servers.InspectAsync(sessionId, requireArmed: true, cancellationToken).ConfigureAwait(false);
            if (!server.Armed || !server.OfflineProven || !server.ObserverReady || observer is null)
                throw new ObserverSpoolException("offline-proof-lost", "The offline server or observer proof no longer passes.");
            await observer.VerifyHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            await observer.HealthAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ObserverSpoolException or InvalidOperationException)
        {
            if (workerControl is not null)
                await workerControl.StopDueToProofLossAsync(failure.Message, CancellationToken.None).ConfigureAwait(false);
            status = status with
            {
                Phase = QaSessionPhase.Faulted,
                OfflineServerArmed = false,
                ClientInputArmed = false,
                BlockerCode = "offline-proof-lost",
                Message = $"The offline server or observer proof no longer passes. Input is disarmed: {failure.Message}",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
        if (workerControl?.State.Phase == WorkerControlPhase.Faulted)
        {
            status = status with
            {
                Phase = QaSessionPhase.Faulted,
                ClientInputArmed = false,
                BlockerCode = workerControl.State.FaultCode ?? "worker-faulted",
                Message = workerControl.State.Message,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }
        return status;
    }

    public async Task<QaSessionStatus> StopAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sessionId = status.SessionId;
            if (sessionId is null) return status;
            status = status with { Phase = QaSessionPhase.Stopping, ClientInputArmed = false, UpdatedAtUtc = DateTimeOffset.UtcNow };
            if (workerControl is not null) await workerControl.DetachAsync(CancellationToken.None).ConfigureAwait(false);
            await servers.StopAsync(sessionId, cancellationToken).ConfigureAwait(false);
            observer = null;
            status = new(null, QaSessionPhase.Idle, false, false, null, null,
                "Offline QA session stopped; data was preserved.", DateTimeOffset.UtcNow);
            return status;
        }
        finally { gate.Release(); }
    }

    private ClientCapability ReadClientCapability()
    {
        using var stream = File.OpenRead(paths.ClientCapabilityPath);
        return JsonSerializer.Deserialize<ClientCapability>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("Client capability record is invalid.");
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    private sealed record ClientCapability(bool DedicatedOfflineCompatible, string BlockerCode);
}
