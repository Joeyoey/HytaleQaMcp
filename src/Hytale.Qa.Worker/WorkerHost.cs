using System.Text.Json;
using Hytale.Qa.Contracts;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Worker;

public sealed class WorkerHost : IDisposable
{
    // The observer publishes at roughly one-second cadence. Allow one missed
    // publication plus scheduler/capture jitter while the orchestrator still
    // requires every proof it forwards to be no older than 1.5 seconds.
    private static readonly TimeSpan OfflineHeartbeatMaximumAge = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ClientLeaseManager leases;
    private readonly FailClosedInputController input;
    private readonly IWindowCaptureBackend capture;
    private readonly IAudioCaptureBackend audio;
    private readonly ITraceSink trace;
    private readonly string artifactRoot;
    private readonly IRollingVideoEncoder? rollingEncoder;
    private CancellationTokenSource? rollingStop;
    private CancellationTokenSource? rollingAbort;
    private Task<RollingVideoCaptureResult>? rollingTask;
    private string? rollingCaptureId;
    private object rollingState = new { status = "idle" };
    private SessionState? session;

    public WorkerHost(IProcessInspector inspector, INativeInputBackend backend, IWindowCaptureBackend capture,
        IAudioCaptureBackend audio, ITraceSink trace, string? artifactRoot = null,
        IRollingVideoEncoder? rollingEncoder = null)
    {
        leases = new(inspector);
        input = new(leases, backend, ValidateArmedSession);
        this.capture = capture;
        this.audio = audio;
        this.trace = trace;
        this.artifactRoot = Path.GetFullPath(artifactRoot ?? Path.Combine(Path.GetTempPath(), "hytale-qa-worker-artifacts"));
        this.rollingEncoder = rollingEncoder;
    }

    public async Task RunAsync(TextReader requests, TextWriter responses, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await requests.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) break;
            line = line.TrimStart('\uFEFF');
            RpcResponse response;
            RpcRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<RpcRequest>(line, JsonOptions)
                    ?? throw new RpcFault(-32600, "Invalid Request");
                response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex) { response = new("2.0", null, Error: new(-32700, "Parse error", ex.Message)); }
            catch (RpcFault ex) { response = new("2.0", ex.Id, Error: new(ex.Code, ex.Message, ex.FaultData)); }
            catch (Exception ex)
            {
                input.ReleaseAll();
                audio.Abort("worker_internal_error");
                AbortRolling();
                response = new("2.0", request?.Id,
                    Error: new(-32603, "Internal error", new { type = ex.GetType().Name, ex.Message }));
            }
            await responses.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            await responses.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RpcResponse> DispatchAsync(RpcRequest request, CancellationToken cancellationToken)
    {
        if (request.JsonRpc != "2.0") throw Fault(request, -32600, "Only JSON-RPC 2.0 is supported.");
        object? result = request.Method switch
        {
            "worker.capabilities" => new WorkerCapabilities(
                "1.1.0", Environment.OSVersion.VersionString, CanLaunchClient: false, CanAttachClient: true,
                SupportsRelativeMouse: true, SupportsClientPointer: true, SupportsHeldKeys: true,
                capture.Capability, audio.Capability),
            "lease.acquire" => Acquire(request),
            "lease.state" => leases.Current,
            "lease.focus" => Focus(request),
            "lease.release" => Release(request),
            "session.arm" => Arm(request),
            "session.heartbeat" => Heartbeat(request),
            "session.state" => EffectiveSessionState(),
            "safety.check" => Safety(request),
            "input.key" => Key(request),
            "input.mouseButton" => MouseButton(request),
            "input.mouseMoveRelative" => MouseMove(request),
            "input.pointerMoveClient" => PointerMoveClient(request),
            "input.releaseAll" => ReleaseAll(request),
            "capture.screenshot" => Capture(request),
            "audio.start" => AudioStart(request),
            "audio.state" => audio.State,
            "audio.stop" => AudioStop(request),
            "audio.abort" => AudioAbort(request),
            "rolling.start" => RollingStart(request),
            "rolling.state" => RollingState(request),
            "rolling.stop" => await RollingStopAsync(request, abort: false, cancellationToken).ConfigureAwait(false),
            "rolling.abort" => await RollingStopAsync(request, abort: true, cancellationToken).ConfigureAwait(false),
            "worker.shutdown" => throw new RpcFault(1000, "Use EOF or process cancellation to stop the worker; shutdown cannot be requested by an untrusted peer.", id: request.Id),
            _ => throw Fault(request, -32601, $"Method not found: {request.Method}")
        };
        return new RpcResponse("2.0", request.Id, result);
    }

    private object Acquire(RpcRequest request)
    {
        var args = Params<AcquireLeaseParams>(request);
        var lease = leases.Acquire(args.ProcessId, args.Policy);
        Trace(lease, "lifecycle", "lease.acquire", "ok", args.CorrelationId, new { lease.Identity });
        return lease;
    }

    private object Arm(RpcRequest request)
    {
        var args = Params<ArmSessionParams>(request);
        var lease = RequireLease(args.LeaseId);
        if (session is not null) throw Fault(request, 2002, "A session has already been armed; mode and capabilities are immutable.");
        var proofCheck = SafetyPolicyValidator.ValidateOfflineProof(lease.Policy, args.OfflineProof, args.Capabilities,
            lease.Identity);
        if (!proofCheck.Safe) throw Fault(request, 2003, $"{proofCheck.Code}: {proofCheck.Message}");
        var leaseCheck = leases.Revalidate(args.LeaseId, false);
        if (!leaseCheck.Safe) throw Fault(request, 2001, $"{leaseCheck.Code}: {leaseCheck.Message}");
        session = new(lease.Policy.SessionId, lease.LeaseId, true, lease.Policy.AssistanceMode,
            args.Capabilities, args.OfflineProof, DateTimeOffset.UtcNow);
        input.Bind(lease.LeaseId);
        Trace(lease, "lifecycle", "session.arm", "ok", args.CorrelationId, new
        {
            session.SessionId,
            session.LeaseId,
            session.Armed,
            session.EvidenceMode,
            session.Capabilities,
            session.ArmedAtUtc,
            proof = new
            {
                args.OfflineProof.AuthMode,
                args.OfflineProof.Offline,
                args.OfflineProof.ServerEndpoint,
                args.OfflineProof.DockerProject,
                args.OfflineProof.DockerContainer,
                args.OfflineProof.DockerNetwork,
                args.OfflineProof.ServerSha256,
                observerNonceSha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(
                            args.OfflineProof.ObserverNonce))),
                args.OfflineProof.ObservedAtUtc
            }
        });
        return session;
    }

    private object Heartbeat(RpcRequest request)
    {
        var args = Params<HeartbeatParams>(request);
        var lease = RequireLease(args.LeaseId);
        if (session is null || !session.Armed || session.LeaseId != args.LeaseId)
            throw Fault(request, 2004, "Session is not armed by a matching offline server proof.");

        var proofCheck = SafetyPolicyValidator.ValidateOfflineProof(
            lease.Policy, args.OfflineProof, session.Capabilities, lease.Identity);
        if (!proofCheck.Safe)
        {
            input.ReleaseAll();
            audio.Abort("offline_proof_rejected");
            AbortRolling();
            session = session with { Armed = false };
            Trace(lease, "safety", "session.heartbeat", "disarmed", args.CorrelationId,
                new { proofCheck.Code, proofCheck.Message });
            throw Fault(request, 2005, $"{proofCheck.Code}: {proofCheck.Message}");
        }

        session = session with { OfflineProof = args.OfflineProof };
        Trace(lease, "safety", "session.heartbeat", "ok", args.CorrelationId,
            new { args.OfflineProof.ObservedAtUtc });
        return session;
    }

    private object Focus(RpcRequest request)
    {
        var args = Params<LeaseParams>(request);
        var focused = leases.TryFocus(args.LeaseId);
        var lease = RequireLease(args.LeaseId);
        Trace(lease, "window", "lease.focus", focused ? "requested" : "rejected", args.CorrelationId, new { focused });
        return new { focused, note = "SetForegroundWindow is best-effort. Input remains blocked until safety.check confirms foreground ownership." };
    }

    private object Release(RpcRequest request)
    {
        var args = Params<LeaseParams>(request);
        var lease = RequireLease(args.LeaseId);
        input.ReleaseAll();
        audio.Abort("lease_released");
        AbortRolling();
        Trace(lease, "lifecycle", "lease.release", "ok", args.CorrelationId, null);
        session = null;
        leases.Release(args.LeaseId);
        return new { released = true };
    }

    private object Safety(RpcRequest request)
    {
        var args = Params<SafetyParams>(request);
        return leases.Revalidate(args.LeaseId, args.RequireForeground) with
        {
            HeldInputCount = input.HeldInputCount
        };
    }

    private object Key(RpcRequest request)
    {
        var args = Params<KeyParams>(request);
        var lease = RequireArmed(args.LeaseId);
        input.Key(args.VirtualKey, args.Transition);
        Trace(lease, "input", "keyboard", "sent", args.CorrelationId, new { args.VirtualKey, args.Transition });
        return new { sent = true };
    }

    private object MouseButton(RpcRequest request)
    {
        var args = Params<MouseButtonParams>(request);
        var lease = RequireArmed(args.LeaseId);
        input.Mouse(args.Button, args.Transition);
        Trace(lease, "input", "mouse.button", "sent", args.CorrelationId, new { args.Button, args.Transition });
        return new { sent = true };
    }

    private object MouseMove(RpcRequest request)
    {
        var args = Params<MouseMoveParams>(request);
        var lease = RequireArmed(args.LeaseId);
        input.MoveRelative(args.DeltaX, args.DeltaY);
        Trace(lease, "input", "mouse.moveRelative", "sent", args.CorrelationId, new { args.DeltaX, args.DeltaY });
        return new { sent = true };
    }

    private object PointerMoveClient(RpcRequest request)
    {
        var args = Params<PointerMoveClientParams>(request);
        var lease = RequireArmed(args.LeaseId);
        input.MoveClient(lease.Identity.WindowHandle, args.NormalizedX, args.NormalizedY);
        Trace(lease, "input", "pointer.moveClient", "sent", args.CorrelationId,
            new { args.NormalizedX, args.NormalizedY, lease.Identity.WindowHandle });
        return new { sent = true };
    }

    private object ReleaseAll(RpcRequest request)
    {
        var args = Params<LeaseParams>(request);
        var lease = RequireArmed(args.LeaseId);
        input.ReleaseAll();
        Trace(lease, "input", "releaseAll", "sent", args.CorrelationId, null);
        return new { released = true };
    }

    private object Capture(RpcRequest request)
    {
        var args = Params<CaptureParams>(request);
        var lease = RequireArmed(args.LeaseId);
        var check = leases.Revalidate(args.LeaseId, !capture.Capability.WorksWhenOccluded);
        if (!check.Safe) throw Fault(request, 2001, $"{check.Code}: {check.Message}");
        var artifact = capture.Capture(lease.Identity, args.DestinationPath);
        Trace(lease, "capture", "screenshot", "ok", args.CorrelationId, artifact);
        return artifact;
    }

    private object AudioStart(RpcRequest request)
    {
        var args = Params<AudioStartParams>(request);
        var lease = RequireArmed(args.LeaseId);
        var check = leases.Revalidate(args.LeaseId, false);
        if (!check.Safe) throw Fault(request, 2001, $"{check.Code}: {check.Message}");
        var state = audio.Start(lease.Identity, args.DestinationPath,
            () => ValidateMediaSession(args.LeaseId));
        Trace(lease, "capture", "audio.start", "ok", args.CorrelationId, state);
        return state;
    }

    private object AudioStop(RpcRequest request)
    {
        var args = Params<AudioStopParams>(request);
        var lease = RequireArmed(args.LeaseId);
        var check = ValidateMediaSession(args.LeaseId);
        if (!check.Safe)
        {
            audio.Abort($"{check.Code}: {check.Message}");
            throw Fault(request, 2001, $"{check.Code}: {check.Message}");
        }
        var artifact = audio.Stop(args.CaptureId);
        Trace(lease, "capture", "audio.stop", "ok", args.CorrelationId, artifact);
        return artifact;
    }

    private object AudioAbort(RpcRequest request)
    {
        var args = Params<AudioAbortParams>(request);
        var lease = RequireLease(args.LeaseId);
        audio.Abort("requested");
        Trace(lease, "capture", "audio.abort", "ok", args.CorrelationId, audio.State);
        return audio.State;
    }

    private object RollingStart(RpcRequest request)
    {
        var args = Params<RollingStartParams>(request);
        var lease = RequireArmed(args.LeaseId);
        if (rollingTask is { IsCompleted: false }) throw Fault(request, 2100, "A rolling capture is already active.");
        if (rollingTask is { IsCompleted: true }) DisposeRollingTokens();
        var destination = ContainedArtifactPath(args.BundleDirectory, directory: true);
        var mp4 = args.FinalMp4Path is null ? null : ContainedArtifactPath(args.FinalMp4Path, directory: false);
        var options = new RollingVideoCaptureOptions(destination, args.FramesPerSecond, args.RingSeconds, mp4);
        options.Validate();
        var captureId = Guid.NewGuid().ToString("N");
        rollingStop = new CancellationTokenSource();
        rollingAbort = new CancellationTokenSource();
        var service = new RollingWindowCaptureService(leases, args.LeaseId, lease.Identity,
            () => ValidateMediaSession(args.LeaseId), encoder: rollingEncoder);
        rollingCaptureId = captureId;
        rollingState = new { captureId, status = "capturing", destination, startedAtUtc = DateTimeOffset.UtcNow };
        rollingTask = Task.Run(async () =>
        {
            try
            {
                var result = await service.CaptureAsync(lease.Identity, options,
                    rollingStop.Token, rollingAbort.Token).ConfigureAwait(false);
                rollingState = result;
                return result;
            }
            catch (Exception failure)
            {
                rollingState = new
                {
                    captureId,
                    status = "faulted",
                    code = "rolling-capture-faulted",
                    failureType = failure.GetType().Name,
                    failure.Message
                };
                throw;
            }
        });
        Trace(lease, "capture", "rolling.start", "ok", args.CorrelationId,
            new { captureId, destination, args.FramesPerSecond, args.RingSeconds, encodeMp4 = mp4 is not null });
        return rollingState;
    }

    private object RollingState(RpcRequest request)
    {
        var args = Params<LeaseParams>(request);
        _ = RequireLease(args.LeaseId);
        if (rollingTask is { IsCompletedSuccessfully: true } completed)
            rollingState = completed.Result;
        else if (rollingTask is { IsFaulted: true } faulted)
        {
            var failure = faulted.Exception?.GetBaseException();
            rollingState = new
            {
                captureId = rollingCaptureId,
                status = "faulted",
                code = "rolling-capture-faulted",
                failureType = failure?.GetType().Name ?? "Unknown",
                message = failure?.Message ?? "Rolling capture faulted without an exception payload."
            };
        }
        else if (rollingTask is { IsCanceled: true })
            rollingState = new { captureId = rollingCaptureId, status = "aborted", code = "rolling-capture-cancelled" };
        return rollingState;
    }

    private async Task<object> RollingStopAsync(RpcRequest request, bool abort, CancellationToken cancellationToken)
    {
        var args = Params<RollingStopParams>(request);
        var lease = RequireLease(args.LeaseId);
        if (rollingTask is null || !string.Equals(args.CaptureId, rollingCaptureId, StringComparison.Ordinal))
            throw Fault(request, 2101, "No matching rolling capture exists.");
        try
        {
            if (abort) rollingAbort?.Cancel(); else rollingStop?.Cancel();
            var result = await rollingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            rollingState = result;
            Trace(lease, "capture", abort ? "rolling.abort" : "rolling.stop", "ok", args.CorrelationId, result);
            return result;
        }
        finally
        {
            DisposeRollingTokens();
        }
    }

    private string ContainedArtifactPath(string value, bool directory)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidOperationException("Rolling artifact path must be absolute.");
        var root = artifactRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(value);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            (!directory && !string.Equals(Path.GetExtension(full), ".mp4", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Rolling artifact path escaped the configured artifact root.");
        return full;
    }

    private void AbortRolling()
    {
        rollingAbort?.Cancel();
    }

    private void DisposeRollingTokens()
    {
        rollingStop?.Dispose();
        rollingAbort?.Dispose();
        rollingStop = null;
        rollingAbort = null;
        rollingTask = null;
        rollingCaptureId = null;
    }

    private SafetyCheck ValidateMediaSession(string leaseId)
    {
        var armed = ValidateArmedSession();
        return armed.Safe ? leases.Revalidate(leaseId, false) : armed;
    }

    private ClientLease RequireLease(string leaseId)
    {
        var lease = leases.Current;
        return lease is not null && lease.LeaseId == leaseId
            ? lease
            : throw new RpcFault(2000, "No matching active lease.");
    }

    private ClientLease RequireArmed(string leaseId)
    {
        var lease = RequireLease(leaseId);
        if (session is null || !session.Armed || session.LeaseId != leaseId)
            throw new RpcFault(2004, "Session is not armed by a matching offline server proof.");
        return lease;
    }

    private SessionState? EffectiveSessionState() => session is null
        ? null
        : session with { Armed = session.Armed && input.WatchdogState.Safe && ValidateArmedSession().Safe };

    private SafetyCheck ValidateArmedSession()
    {
        var current = session;
        if (current is null || !current.Armed)
            return SafetyCheck.Fail("session.not_armed", "A matching offline server proof has not armed the session.");
        var age = DateTimeOffset.UtcNow - current.OfflineProof.ObservedAtUtc;
        return age >= TimeSpan.Zero && age <= OfflineHeartbeatMaximumAge
            ? SafetyCheck.Pass("offline observer heartbeat fresh")
            : SafetyCheck.Fail("proof.heartbeat_stale", "The offline observer heartbeat is stale; input is disarmed.");
    }

    private void Trace(ClientLease lease, string category, string action, string outcome, string correlationId, object? data) =>
        trace.Append(lease.Policy.SessionId, category, action, outcome, correlationId, data);

    private static T Params<T>(RpcRequest request) where T : class
    {
        if (request.Params is not JsonElement parameters)
            throw Fault(request, -32602, "Missing or invalid params.");
        return parameters.Deserialize<T>(JsonOptions)
            ?? throw Fault(request, -32602, "Missing or invalid params.");
    }

    private static RpcFault Fault(RpcRequest request, int code, string message) => new(code, message, id: request.Id);

    public void Dispose()
    {
        input.Dispose();
        audio.Abort("worker_disposed");
        AbortRolling();
        try { rollingTask?.GetAwaiter().GetResult(); } catch { }
        DisposeRollingTokens();
        audio.Dispose();
        trace.Dispose();
    }

    public sealed record AcquireLeaseParams(int ProcessId, SessionSafetyPolicy Policy, string CorrelationId);
    public sealed record ArmSessionParams(string LeaseId, string CorrelationId, OfflineServerProof OfflineProof, EvidenceCapabilities Capabilities) : LeaseParams(LeaseId, CorrelationId);
    public sealed record HeartbeatParams(string LeaseId, string CorrelationId, OfflineServerProof OfflineProof) : LeaseParams(LeaseId, CorrelationId);
    public record LeaseParams(string LeaseId, string CorrelationId);
    public sealed record SafetyParams(string LeaseId, string CorrelationId, bool RequireForeground = true) : LeaseParams(LeaseId, CorrelationId);
    public sealed record KeyParams(string LeaseId, string CorrelationId, ushort VirtualKey, KeyTransition Transition) : LeaseParams(LeaseId, CorrelationId);
    public sealed record MouseButtonParams(string LeaseId, string CorrelationId, Hytale.Qa.Contracts.MouseButton Button, KeyTransition Transition) : LeaseParams(LeaseId, CorrelationId);
    public sealed record MouseMoveParams(string LeaseId, string CorrelationId, int DeltaX, int DeltaY) : LeaseParams(LeaseId, CorrelationId);
    public sealed record PointerMoveClientParams(string LeaseId, string CorrelationId,
        double NormalizedX, double NormalizedY) : LeaseParams(LeaseId, CorrelationId);
    public sealed record CaptureParams(string LeaseId, string CorrelationId, string DestinationPath) : LeaseParams(LeaseId, CorrelationId);
    public sealed record AudioStartParams(string LeaseId, string CorrelationId, string DestinationPath) : LeaseParams(LeaseId, CorrelationId);
    public sealed record AudioStopParams(string LeaseId, string CorrelationId, string CaptureId) : LeaseParams(LeaseId, CorrelationId);
    public sealed record AudioAbortParams(string LeaseId, string CorrelationId) : LeaseParams(LeaseId, CorrelationId);
    public sealed record RollingStartParams(string LeaseId, string CorrelationId, string BundleDirectory,
        int FramesPerSecond, int RingSeconds, string? FinalMp4Path) : LeaseParams(LeaseId, CorrelationId);
    public sealed record RollingStopParams(string LeaseId, string CorrelationId, string CaptureId) : LeaseParams(LeaseId, CorrelationId);
}

public sealed class RpcFault : Exception
{
    public RpcFault(int code, string message, object? data = null, JsonElement? id = null) : base(message)
    { Code = code; FaultData = data; Id = id; }
    public int Code { get; }
    public object? FaultData { get; }
    public JsonElement? Id { get; }
}
