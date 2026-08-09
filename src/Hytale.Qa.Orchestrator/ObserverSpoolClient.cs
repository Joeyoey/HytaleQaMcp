using System.Security;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hytale.Qa.Orchestrator;

public sealed record ObserverHeartbeat(
    bool Ready,
    bool Offline,
    string EffectiveAuthMode,
    string SessionNonceSha256,
    bool PacketEvidenceValid,
    long LastSequence,
    DateTimeOffset ObservedAt,
    string BridgeNetworkBoundary = "");

public sealed record ObserverHealth(
    bool Ready,
    bool Offline,
    string EffectiveAuthMode,
    string BridgeNetworkBoundary,
    string SessionNonceSha256,
    bool PacketEvidenceValid);

public sealed record ObserverObservation(
    long ObservationSequence,
    DateTimeOffset ObservedAt,
    bool EvidenceValid,
    JsonElement PacketBatch,
    JsonElement WorldSnapshot,
    JsonElement Raw);

public sealed class ObserverSpoolException(string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public string Code { get; } = code;
}

public interface IObserverSpoolClient
{
    Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken);
    Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken);
    Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken);
    Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("observer-sole-player-not-implemented");
}

public interface IObserverSpoolClientFactory
{
    IObserverSpoolClient Create(string spoolRoot, string sessionNonce);
    IObserverSpoolClient Create(string spoolRoot, string sessionNonce, string expectedHealthBoundary) =>
        string.Equals(expectedHealthBoundary, "DOCKER_SHARED_VOLUME_SPOOL", StringComparison.Ordinal)
            ? Create(spoolRoot, sessionNonce)
            : throw new NotSupportedException("observer-boundary-factory-not-implemented");
}

public sealed class ObserverSpoolClientFactory : IObserverSpoolClientFactory
{
    public IObserverSpoolClient Create(string spoolRoot, string sessionNonce) =>
        new ObserverSpoolClient(spoolRoot, sessionNonce);
    public IObserverSpoolClient Create(string spoolRoot, string sessionNonce, string expectedHealthBoundary) =>
        new ObserverSpoolClient(spoolRoot, sessionNonce, expectedHealthBoundary: expectedHealthBoundary);
}

public sealed class ObserverSpoolClient : IObserverSpoolClient
{
    private static readonly ConcurrentDictionary<string, SharedRequestSequence> SharedSequences =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumHeartbeatBytes = 64 * 1024;
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromSeconds(1);
    private static readonly Regex VersionedHeartbeatName = new(
        "^heartbeat-[0-9]{20}-[0-9a-fA-F-]{36}\\.json$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string spoolRoot;
    private readonly string requestsDirectory;
    private readonly string responsesDirectory;
    private readonly string eventsDirectory;
    private readonly string sequenceLockPath;
    private readonly string sequenceLedgerPath;
    private readonly string sessionNonce;
    private readonly string expectedNonceSha256;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan maximumHeartbeatAge;
    private readonly TimeSpan responseTimeout;
    private readonly TimeSpan pollInterval;
    private readonly string expectedHealthBoundary;
    private readonly SharedRequestSequence requestSequence;

    public ObserverSpoolClient(
        string spoolRoot,
        string sessionNonce,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumHeartbeatAge = null,
        TimeSpan? responseTimeout = null,
        TimeSpan? pollInterval = null,
        string expectedHealthBoundary = "DOCKER_SHARED_VOLUME_SPOOL")
    {
        if (string.IsNullOrWhiteSpace(spoolRoot))
            throw new ArgumentException("Spool root is required.", nameof(spoolRoot));
        if (string.IsNullOrWhiteSpace(sessionNonce))
            throw new ArgumentException("Session nonce is required.", nameof(sessionNonce));

        this.spoolRoot = Path.GetFullPath(spoolRoot);
        requestsDirectory = ContainedChild(this.spoolRoot, "requests");
        responsesDirectory = ContainedChild(this.spoolRoot, "responses");
        eventsDirectory = ContainedChild(this.spoolRoot, "events");
        sequenceLockPath = ContainedChild(this.spoolRoot, "client-sequence.lock");
        sequenceLedgerPath = ContainedChild(this.spoolRoot, "client-sequence.json");
        this.sessionNonce = sessionNonce;
        expectedNonceSha256 = Sha256(Encoding.UTF8.GetBytes(sessionNonce));
        requestSequence = SharedSequences.GetOrAdd(
            this.spoolRoot.TrimEnd(Path.DirectorySeparatorChar) + "|" + expectedNonceSha256,
            _ => new SharedRequestSequence());
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.maximumHeartbeatAge = maximumHeartbeatAge ?? TimeSpan.FromSeconds(3);
        this.responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(8);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(20);
        this.expectedHealthBoundary = string.IsNullOrWhiteSpace(expectedHealthBoundary)
            ? throw new ArgumentException("Expected observer health boundary is required.", nameof(expectedHealthBoundary))
            : expectedHealthBoundary;
        if (this.maximumHeartbeatAge <= TimeSpan.Zero || this.responseTimeout <= TimeSpan.Zero || this.pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumHeartbeatAge), "Observer timeouts must be positive.");
    }

    public async Task<ObserverHeartbeat> VerifyHeartbeatAsync(CancellationToken cancellationToken)
    {
        RequireSafeDirectory(spoolRoot, allowMissing: false);
        RequireSafeDirectory(eventsDirectory, allowMissing: false);
        var candidates = Directory.EnumerateFiles(eventsDirectory, "heartbeat-*.json", SearchOption.TopDirectoryOnly)
            .Where(path => VersionedHeartbeatName.IsMatch(Path.GetFileName(path)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        var heartbeatPath = candidates.FirstOrDefault() ?? Path.Combine(eventsDirectory, "heartbeat.json");
        var bytes = await ReadBoundedFileAsync(heartbeatPath, MaximumHeartbeatBytes, cancellationToken).ConfigureAwait(false);

        HeartbeatEnvelope envelope;
        JsonElement heartbeatRoot;
        try
        {
            using var document = JsonDocument.Parse(bytes);
            heartbeatRoot = document.RootElement.Clone();
            envelope = JsonSerializer.Deserialize<HeartbeatEnvelope>(bytes, JsonOptions)
                ?? throw Protocol("observer-heartbeat-invalid", "Observer heartbeat is empty.");
        }
        catch (JsonException failure)
        {
            throw Protocol("observer-heartbeat-invalid", "Observer heartbeat JSON is invalid.", failure);
        }

        if (!string.Equals(envelope.Type, "spool-heartbeat", StringComparison.Ordinal) || envelope.SchemaVersion != 2)
            throw Protocol("observer-heartbeat-schema-invalid", "Observer heartbeat schema is not supported.");
        if (!envelope.Ready || !envelope.Offline || !envelope.PacketEvidenceValid ||
            !string.Equals(envelope.EffectiveAuthMode, "OFFLINE", StringComparison.Ordinal) ||
            !string.Equals(envelope.BridgeNetworkBoundary, expectedHealthBoundary, StringComparison.Ordinal))
            throw Protocol("observer-heartbeat-not-ready", "Observer heartbeat does not attest ready, offline, valid evidence.");
        RequireNonceHash(envelope.SessionNonceSha256, "observer-heartbeat-nonce-mismatch");
        if (envelope.LastSequence < 0)
            throw Protocol("observer-heartbeat-sequence-invalid", "Observer heartbeat sequence is negative.");
        var observedAtRaw = String(heartbeatRoot, "observedAt");
        var heartbeatCanonical = new StringBuilder(384)
            .Append("HYTALE-QA-SPOOL-HEARTBEAT/1\n")
            .Append("ready=").Append(envelope.Ready.ToString().ToLowerInvariant()).Append('\n')
            .Append("offline=").Append(envelope.Offline.ToString().ToLowerInvariant()).Append('\n')
            .Append("effectiveAuthMode=").Append(envelope.EffectiveAuthMode).Append('\n')
            .Append("bridgeNetworkBoundary=").Append(envelope.BridgeNetworkBoundary).Append('\n')
            .Append("sessionNonceSha256=").Append(envelope.SessionNonceSha256).Append('\n')
            .Append("packetEvidenceValid=").Append(envelope.PacketEvidenceValid.ToString().ToLowerInvariant()).Append('\n')
            .Append("lastSequence=").Append(envelope.LastSequence).Append('\n')
            .Append("observedAt=").Append(observedAtRaw).Append('\n')
            .ToString();
        RequireMac(heartbeatCanonical, envelope.Mac, "observer-heartbeat-mac-invalid");

        var age = timeProvider.GetUtcNow() - envelope.ObservedAt;
        if (age < -MaximumFutureClockSkew || age > maximumHeartbeatAge)
            throw Protocol("observer-heartbeat-stale", "Observer heartbeat is stale or future-dated.");

        return new(envelope.Ready, envelope.Offline, envelope.EffectiveAuthMode ?? "",
            envelope.SessionNonceSha256 ?? "", envelope.PacketEvidenceValid, envelope.LastSequence,
            envelope.ObservedAt, envelope.BridgeNetworkBoundary ?? "");
    }

    public async Task<ObserverHealth> HealthAsync(CancellationToken cancellationToken)
    {
        var payload = await SendAsync("HEALTH", null, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(String(payload, "type"), "health", StringComparison.Ordinal) || Integer(payload, "schemaVersion") != 1)
            throw Protocol("observer-health-schema-invalid", "Observer HEALTH response schema is not supported.");
        var health = new ObserverHealth(
            Boolean(payload, "ready"),
            Boolean(payload, "offline"),
            String(payload, "effectiveAuthMode"),
            String(payload, "bridgeNetworkBoundary"),
            String(payload, "sessionNonceSha256"),
            Boolean(payload, "packetEvidenceValid"));
        if (!health.Ready || !health.Offline || !health.PacketEvidenceValid ||
            !string.Equals(health.EffectiveAuthMode, "OFFLINE", StringComparison.Ordinal) ||
            !string.Equals(health.BridgeNetworkBoundary, expectedHealthBoundary, StringComparison.Ordinal))
            throw Protocol("observer-health-not-ready", $"Observer HEALTH response does not attest the expected {expectedHealthBoundary} boundary.");
        RequireNonceHash(health.SessionNonceSha256, "observer-health-nonce-mismatch");
        return health;
    }

    public async Task<ObserverObservation> ObserveAsync(Guid playerId, CancellationToken cancellationToken)
    {
        if (playerId == Guid.Empty) throw new ArgumentException("Player id cannot be empty.", nameof(playerId));
        var payload = await SendAsync("OBSERVE", playerId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(String(payload, "type"), "observation", StringComparison.Ordinal) || Integer(payload, "schemaVersion") != 1)
            throw Protocol("observer-observation-schema-invalid", "Observer OBSERVE response schema is not supported.");
        var observation = new ObserverObservation(
            Long(payload, "observationSequence"),
            DateTimeOffset.Parse(String(payload, "observedAt"), System.Globalization.CultureInfo.InvariantCulture),
            Boolean(payload, "evidenceValid"),
            Element(payload, "packetBatch"),
            Element(payload, "worldSnapshot"),
            payload.Clone());
        if (!observation.EvidenceValid ||
            !Boolean(observation.PacketBatch, "valid") ||
            Long(observation.PacketBatch, "droppedFacts") != 0)
            throw Protocol("observer-evidence-invalid", "Observer returned invalid or incomplete packet evidence.");
        return observation;
    }

    public async Task<ObserverObservation> ObserveSolePlayerAsync(CancellationToken cancellationToken)
    {
        var payload = await SendAsync("OBSERVE_SOLE_PLAYER", null, cancellationToken).ConfigureAwait(false);
        return ParseObservation(payload);
    }

    private static ObserverObservation ParseObservation(JsonElement payload)
    {
        if (!string.Equals(String(payload, "type"), "observation", StringComparison.Ordinal) || Integer(payload, "schemaVersion") != 1)
            throw Protocol("observer-observation-schema-invalid", "Observer observation response schema is not supported.");
        var observation = new ObserverObservation(
            Long(payload, "observationSequence"),
            DateTimeOffset.Parse(String(payload, "observedAt"), System.Globalization.CultureInfo.InvariantCulture),
            Boolean(payload, "evidenceValid"), Element(payload, "packetBatch"), Element(payload, "worldSnapshot"), payload.Clone());
        if (!observation.EvidenceValid || !Boolean(observation.PacketBatch, "valid") ||
            Long(observation.PacketBatch, "droppedFacts") != 0)
            throw Protocol("observer-evidence-invalid", "Observer returned invalid or incomplete packet evidence.");
        return observation;
    }

    private async Task<JsonElement> SendAsync(string command, Guid? playerId, CancellationToken cancellationToken)
    {
        await requestSequence.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var sequenceLease = await AcquireSequenceLeaseAsync(cancellationToken).ConfigureAwait(false);
            var heartbeat = await VerifyHeartbeatAsync(cancellationToken).ConfigureAwait(false);
            var durableSequence = await ReadSequenceLedgerAsync(cancellationToken).ConfigureAwait(false);
            requestSequence.LastIssued = Math.Max(requestSequence.LastIssued,
                Math.Max(heartbeat.LastSequence, durableSequence));
            if (requestSequence.LastIssued == long.MaxValue)
                throw Protocol("observer-sequence-exhausted", "Observer request sequence is exhausted.");
            var sequence = ++requestSequence.LastIssued;
            await WriteSequenceLedgerAsync(sequence, cancellationToken).ConfigureAwait(false);
            var requestId = Guid.NewGuid();
            var stem = $"{sequence:00000000000000000000}-{requestId:D}";
            var requestPath = Path.Combine(requestsDirectory, stem + ".request");
            var responsePath = Path.Combine(responsesDirectory, stem + ".response.ndjson");
            var body = BuildRequest(requestId, sequence, command, playerId);
            var requestBytes = Encoding.UTF8.GetBytes(body);
            var requestHash = Sha256(requestBytes);

            RequireSafeDirectory(requestsDirectory, allowMissing: false);
            RequireSafeDirectory(responsesDirectory, allowMissing: false);
            await AtomicPublishAsync(requestPath, requestBytes, cancellationToken).ConfigureAwait(false);
            return await WaitForResponseAsync(responsePath, requestId, sequence, requestHash, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            requestSequence.Gate.Release();
        }
    }

    private async Task<FileStream> AcquireSequenceLeaseAsync(CancellationToken cancellationToken)
    {
        RequireSafeDirectory(spoolRoot, allowMissing: false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(sequenceLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
                if ((File.GetAttributes(sequenceLockPath) & FileAttributes.ReparsePoint) != 0)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw Protocol("observer-sequence-lock-unsafe", "Observer sequence lock cannot be a reparse point.");
                }
                return stream;
            }
            catch (IOException)
            {
                await Task.Delay(pollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<long> ReadSequenceLedgerAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(sequenceLedgerPath)) return 0;
        var bytes = await ReadBoundedFileAsync(sequenceLedgerPath, MaximumHeartbeatBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (Integer(root, "schemaVersion") != 1 ||
                !string.Equals(String(root, "type"), "client-sequence", StringComparison.Ordinal))
                throw Protocol("observer-sequence-ledger-invalid", "Observer sequence ledger schema is invalid.");
            var nonceSha256 = String(root, "sessionNonceSha256");
            RequireNonceHash(nonceSha256, "observer-sequence-ledger-nonce-mismatch");
            var sequence = Long(root, "lastIssuedSequence");
            if (sequence < 0) throw Protocol("observer-sequence-ledger-invalid", "Observer sequence ledger is negative.");
            RequireMac(SequenceCanonical(nonceSha256, sequence), String(root, "mac"),
                "observer-sequence-ledger-mac-invalid");
            return sequence;
        }
        catch (JsonException failure)
        {
            throw Protocol("observer-sequence-ledger-invalid", "Observer sequence ledger JSON is invalid.", failure);
        }
    }

    private async Task WriteSequenceLedgerAsync(long sequence, CancellationToken cancellationToken)
    {
        var canonical = SequenceCanonical(expectedNonceSha256, sequence);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sessionNonce));
        var mac = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "client-sequence", schemaVersion = 1, sessionNonceSha256 = expectedNonceSha256,
            lastIssuedSequence = sequence, mac
        });
        var temporary = Path.Combine(spoolRoot, $".client-sequence-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, sequenceLedgerPath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } }
    }

    private static string SequenceCanonical(string nonceSha256, long sequence) => new StringBuilder(192)
        .Append("HYTALE-QA-SPOOL-CLIENT-SEQUENCE/1\n")
        .Append("sessionNonceSha256=").Append(nonceSha256).Append('\n')
        .Append("lastIssuedSequence=").Append(sequence).Append('\n')
        .ToString();

    private string BuildRequest(Guid requestId, long sequence, string command, Guid? playerId)
    {
        var builder = new StringBuilder(256)
            .Append("HYTALE-QA-SPOOL/2\n")
            .Append("requestId=").Append(requestId.ToString("D")).Append('\n')
            .Append("sequence=").Append(sequence).Append('\n')
            .Append("createdAt=").Append(timeProvider.GetUtcNow().ToString("O")).Append('\n')
            .Append("command=").Append(command).Append('\n');
        if (playerId.HasValue) builder.Append("playerId=").Append(playerId.Value.ToString("D")).Append('\n');
        var unsigned = builder.ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sessionNonce));
        var authenticationTag = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
        return unsigned + "mac=" + authenticationTag + "\n";
    }

    private async Task<JsonElement> WaitForResponseAsync(
        string responsePath,
        Guid requestId,
        long sequence,
        string requestHash,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(responseTimeout);
        try
        {
            while (!File.Exists(responsePath))
                await Task.Delay(pollInterval, timeProvider, timeout.Token).ConfigureAwait(false);
            var bytes = await ReadBoundedFileAsync(responsePath, MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            var lines = Encoding.UTF8.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != 2)
                throw Protocol("observer-response-envelope-invalid", "Observer response must contain exactly metadata and payload lines.");
            using var metadataDocument = JsonDocument.Parse(lines[0]);
            var metadata = metadataDocument.RootElement;
            if (!string.Equals(String(metadata, "type"), "spool-response", StringComparison.Ordinal) ||
                Integer(metadata, "schemaVersion") != 2 ||
                !Guid.TryParse(String(metadata, "requestId"), out var responseId) || responseId != requestId ||
                Long(metadata, "sequence") != sequence ||
                !FixedEquals(String(metadata, "requestSha256"), requestHash) ||
                !FixedEquals(String(metadata, "payloadSha256"), Sha256(Encoding.UTF8.GetBytes(lines[1]))))
                throw Protocol("observer-response-evidence-mismatch", "Observer response does not match the request identity and SHA-256.");
            var responseCanonical = new StringBuilder(384)
                .Append("HYTALE-QA-SPOOL-RESPONSE/1\n")
                .Append("requestId=").Append(requestId.ToString("D")).Append('\n')
                .Append("sequence=").Append(sequence).Append('\n')
                .Append("requestSha256=").Append(requestHash).Append('\n')
                .Append("payloadSha256=").Append(String(metadata, "payloadSha256")).Append('\n')
                .ToString();
            RequireMac(responseCanonical, String(metadata, "mac"), "observer-response-mac-invalid");

            using var payloadDocument = JsonDocument.Parse(lines[1]);
            var payload = payloadDocument.RootElement.Clone();
            if (string.Equals(String(payload, "type"), "error", StringComparison.Ordinal))
            {
                var code = String(payload, "code");
                throw Protocol("observer-server-" + code, $"Observer rejected the request: {code}.");
            }
            return payload;
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested)
        {
            throw Protocol("observer-response-timeout", "Timed out waiting for the observer response.", failure);
        }
        catch (JsonException failure)
        {
            throw Protocol("observer-response-json-invalid", "Observer response JSON is invalid.", failure);
        }
    }

    private static async Task AtomicPublishAsync(string destination, byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Request path has no directory.");
        var temporary = Path.Combine(directory, $".hytale-qa-client-{Guid.NewGuid():D}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw Protocol("observer-file-missing", $"Observer file is missing: {Path.GetFileName(path)}.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length < 1 || info.Length > maximumBytes)
            throw Protocol("observer-file-unsafe", $"Observer file is unsafe or oversized: {info.Name}.");
        var bytes = new byte[checked((int)info.Length)];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw Protocol("observer-file-truncated", $"Observer file changed while reading: {info.Name}.");
            offset += read;
        }
        if (stream.ReadByte() != -1)
            throw Protocol("observer-file-grew", $"Observer file changed while reading: {info.Name}.");
        return bytes;
    }

    private static void RequireSafeDirectory(string path, bool allowMissing)
    {
        if (!Directory.Exists(path))
        {
            if (allowMissing) return;
            throw Protocol("observer-directory-missing", $"Observer directory is missing: {Path.GetFileName(path)}.");
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw Protocol("observer-directory-unsafe", $"Observer directory cannot be a reparse point: {Path.GetFileName(path)}.");
    }

    private static string ContainedChild(string root, string child)
    {
        var path = Path.GetFullPath(Path.Combine(root, child));
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new SecurityException("Observer path escaped its spool root.");
        return path;
    }

    private void RequireNonceHash(string? actual, string code)
    {
        if (!FixedEquals(actual ?? "", expectedNonceSha256))
            throw Protocol(code, "Observer nonce fingerprint does not match this session.");
    }

    private void RequireMac(string canonical, string? actual, string code)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sessionNonce));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        if (!FixedEquals(actual ?? "", expected)) throw Protocol(code, "Observer authentication tag is invalid.");
    }

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ObserverSpoolException Protocol(string code, string message, Exception? inner = null) => new(code, message, inner);

    private static JsonElement Element(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.Clone() : throw Protocol("observer-field-missing", $"Observer field is missing: {property}.");
    private static string String(JsonElement element, string property) =>
        Element(element, property).ValueKind == JsonValueKind.String
            ? element.GetProperty(property).GetString() ?? ""
            : throw Protocol("observer-field-invalid", $"Observer field is not a string: {property}.");
    private static bool Boolean(JsonElement element, string property) =>
        Element(element, property).ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetProperty(property).GetBoolean()
            : throw Protocol("observer-field-invalid", $"Observer field is not a boolean: {property}.");
    private static long Long(JsonElement element, string property) =>
        Element(element, property).TryGetInt64(out var value)
            ? value
            : throw Protocol("observer-field-invalid", $"Observer field is not an integer: {property}.");
    private static int Integer(JsonElement element, string property) => checked((int)Long(element, property));

    private sealed record HeartbeatEnvelope(
        string? Type,
        int SchemaVersion,
        bool Ready,
        bool Offline,
        string? EffectiveAuthMode,
        string? BridgeNetworkBoundary,
        string? SessionNonceSha256,
        bool PacketEvidenceValid,
        long LastSequence,
        DateTimeOffset ObservedAt,
        string? Mac);

    private sealed class SharedRequestSequence
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long LastIssued;
    }
}
