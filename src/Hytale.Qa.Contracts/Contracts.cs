using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hytale.Qa.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssistanceMode { BlackBox, GuidedPhysical, WhiteBox }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum KeyTransition { Down, Up }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MouseButton { Left, Right, Middle, X1, X2 }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureAvailability { Available, Unsupported, TemporarilyUnavailable }

public sealed record SessionSafetyPolicy(
    string SessionId,
    string Nonce,
    AssistanceMode AssistanceMode,
    bool OfflineAttested,
    string ServerEndpoint,
    string ExpectedExecutableName,
    IReadOnlyList<string> AllowedExecutablePaths,
    IReadOnlyList<string> AllowedSha256,
    bool RequireSingleMatchingProcess = true);

public sealed record ProcessIdentity(
    int ProcessId,
    DateTimeOffset CreationTimeUtc,
    string ExecutablePath,
    string Sha256,
    long WindowHandle);

public sealed record ClientLease(
    string LeaseId,
    DateTimeOffset AcquiredAtUtc,
    ProcessIdentity Identity,
    SessionSafetyPolicy Policy);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OfflineProofKind { DockerDedicated, LauncherOwnedSingleplayer }

public sealed record PinnedProcessEvidence(
    int ProcessId,
    int ParentProcessId,
    DateTimeOffset CreationTimeUtc,
    string ExecutablePath,
    string Sha256);

public sealed record LauncherSingleplayerEvidence(
    string EvidenceId,
    string WorldId,
    string WorldPathSha256,
    string WorldManifestSha256,
    PinnedProcessEvidence Launcher,
    PinnedProcessEvidence Client,
    PinnedProcessEvidence Server,
    string ServerArtifactSha256,
    string AssetsSha256,
    string LaunchEvidenceSha256,
    bool ProcessTreeVerified,
    bool LoopbackOnlyVerified,
    bool ObserverBoundaryVerified);

public sealed record OfflineServerProof(
    string AuthMode,
    bool Offline,
    string ServerEndpoint,
    string DockerProject,
    string DockerContainer,
    string DockerNetwork,
    string ServerSha256,
    string ObserverNonce,
    DateTimeOffset ObservedAtUtc,
    OfflineProofKind Kind = OfflineProofKind.DockerDedicated,
    LauncherSingleplayerEvidence? Launcher = null);

public sealed record EvidenceCapabilities(
    bool PhysicalInput,
    bool ReadOnlyServerObserver,
    bool CombatReflex,
    bool Teleport,
    bool DirectDamage,
    bool FaultInjection);

public sealed record SessionState(
    string SessionId,
    string LeaseId,
    bool Armed,
    AssistanceMode EvidenceMode,
    EvidenceCapabilities Capabilities,
    OfflineServerProof OfflineProof,
    DateTimeOffset ArmedAtUtc);

public sealed record SafetyCheck(bool Safe, string Code, string Message, int HeldInputCount = 0)
{
    public static SafetyCheck Pass(string message = "safe") => new(true, "ok", message);
    public static SafetyCheck Fail(string code, string message) => new(false, code, message);
}

public sealed record CaptureCapability(
    string Backend,
    CaptureAvailability Availability,
    bool WorksWhenOccluded,
    bool WorksWhenMinimized,
    IReadOnlyList<string> Formats,
    string Limitation);

public sealed record CaptureArtifact(
    string Path,
    string MediaType,
    int Width,
    int Height,
    DateTimeOffset CapturedAtUtc,
    string Sha256,
    CaptureCapability Capability);

public sealed record AudioCaptureCapability(
    string Backend,
    CaptureAvailability Availability,
    bool ProcessIsolated,
    bool IncludesChildProcesses,
    IReadOnlyList<string> Formats,
    int MinimumWindowsBuild,
    string Limitation);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCaptureStatus { Idle, Capturing, Completed, Aborted, Failed }

public sealed record AudioCaptureState(
    string? CaptureId,
    AudioCaptureStatus Status,
    int? ProcessId,
    string? DestinationPath,
    DateTimeOffset? StartedAtUtc,
    string? StopReason,
    AudioCaptureCapability Capability);

public sealed record AudioCaptureArtifact(
    string CaptureId,
    string Path,
    string MediaType,
    long Bytes,
    TimeSpan Duration,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string Sha256,
    AudioCaptureCapability Capability);

public sealed record TraceRecord(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string SessionId,
    string Category,
    string Action,
    string Outcome,
    string CorrelationId,
    JsonElement Data,
    string PreviousHash,
    string Hash);

public sealed record RpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonElement? Params);

public sealed record RpcError(int Code, string Message, object? Data = null);

public sealed record RpcResponse(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] JsonElement? Id,
    [property: JsonPropertyName("result")] object? Result = null,
    [property: JsonPropertyName("error")] RpcError? Error = null);

public sealed record WorkerCapabilities(
    string Version,
    string Platform,
    bool CanLaunchClient,
    bool CanAttachClient,
    bool SupportsRelativeMouse,
    bool SupportsClientPointer,
    bool SupportsHeldKeys,
    CaptureCapability Capture,
    AudioCaptureCapability AudioCapture);
