using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class ObserverSpoolClientTests : IDisposable
{
    private const string Nonce = "8AFBD4D029367133584B41C07A99E305FBF47A7C42B9F1B052330EB24271920F";
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-spool-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task HealthUsesMonotonicSequenceAndValidatesRequestHash()
    {
        CreateSpool(lastSequence: 41);
        var client = Client();
        var server = RespondOnceAsync(payload: HealthPayload(), expectedSequence: 42);

        var health = await client.HealthAsync(CancellationToken.None);
        await server;

        Assert.True(health.Ready);
        Assert.Equal("DOCKER_SHARED_VOLUME_SPOOL", health.BridgeNetworkBoundary);
    }

    [Fact]
    public async Task RepeatedCommandsAdvanceSequenceEvenBeforeHeartbeatCatchesUp()
    {
        CreateSpool(lastSequence: 100);
        var client = Client();
        var firstServer = RespondOnceAsync(HealthPayload(), expectedSequence: 101);
        await client.HealthAsync(CancellationToken.None);
        await firstServer;
        var firstRequest = Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request").Single();
        File.Delete(firstRequest);

        var secondServer = RespondOnceAsync(HealthPayload(), expectedSequence: 102);
        await client.HealthAsync(CancellationToken.None);
        await secondServer;
    }

    [Fact]
    public async Task SeparateClientsShareSequenceStateForTheSameAuthenticatedSpool()
    {
        CreateSpool(lastSequence: 0);
        var first = Client();
        var second = Client();
        var firstServer = RespondOnceAsync(HealthPayload(), expectedSequence: 1);
        await first.HealthAsync(CancellationToken.None);
        await firstServer;
        File.Delete(Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request")));

        var secondServer = RespondOnceAsync(HealthPayload(), expectedSequence: 2);
        await second.HealthAsync(CancellationToken.None);
        await secondServer;
        Assert.True(File.Exists(Path.Combine(root, "client-sequence.json")));
    }

    [Fact]
    public async Task OsSequenceLeaseSerializesOtherProcessesAndTamperedLedgerFailsClosed()
    {
        CreateSpool(lastSequence: 0);
        await using (var held = new FileStream(Path.Combine(root, "client-sequence.lock"), FileMode.OpenOrCreate,
                         FileAccess.ReadWrite, FileShare.None))
        {
            using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(75));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().HealthAsync(cancelled.Token));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request"));
        }

        var server = RespondOnceAsync(HealthPayload(), expectedSequence: 1);
        await Client().HealthAsync(CancellationToken.None);
        await server;
        var ledgerPath = Path.Combine(root, "client-sequence.json");
        var ledger = await File.ReadAllTextAsync(ledgerPath);
        await File.WriteAllTextAsync(ledgerPath, ledger.Replace("\"lastIssuedSequence\":1", "\"lastIssuedSequence\":9"));
        File.Delete(Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request")));

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(() => Client().HealthAsync(CancellationToken.None));
        Assert.Equal("observer-sequence-ledger-mac-invalid", failure.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request"));
    }

    [Fact]
    public async Task ObserveReturnsOnlyCompleteEvidence()
    {
        CreateSpool(lastSequence: 2);
        var playerId = Guid.NewGuid();
        var client = Client();
        var server = RespondOnceAsync(ObservationPayload(playerId), expectedSequence: 3, expectedPlayerId: playerId);

        var observation = await client.ObserveAsync(playerId, CancellationToken.None);
        await server;

        Assert.True(observation.EvidenceValid);
        Assert.Equal(7, observation.ObservationSequence);
        Assert.Equal(playerId.ToString(), observation.WorldSnapshot.GetProperty("playerId").GetString());
    }

    [Fact]
    public async Task ObserveSolePlayerDoesNotTransmitAPlayerIdentifier()
    {
        CreateSpool(lastSequence: 12);
        var client = Client();
        var server = RespondOnceAsync(ObservationPayload(Guid.NewGuid()), expectedSequence: 13,
            expectedCommand: "OBSERVE_SOLE_PLAYER");

        var observation = await client.ObserveSolePlayerAsync(CancellationToken.None);
        await server;

        Assert.True(observation.EvidenceValid);
    }

    [Fact]
    public async Task RejectsResponseWhoseRequestHashDoesNotMatch()
    {
        CreateSpool(lastSequence: 0);
        var client = Client();
        var server = RespondOnceAsync(HealthPayload(), expectedSequence: 1, corruptHash: true);

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => client.HealthAsync(CancellationToken.None));
        await server;

        Assert.Equal("observer-response-evidence-mismatch", failure.Code);
    }

    [Fact]
    public async Task RejectsResponseWhoseAuthenticationTagDoesNotMatch()
    {
        CreateSpool(lastSequence: 0);
        var client = Client();
        var server = RespondOnceAsync(HealthPayload(), expectedSequence: 1, corruptMac: true);

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => client.HealthAsync(CancellationToken.None));
        await server;

        Assert.Equal("observer-response-mac-invalid", failure.Code);
    }

    [Fact]
    public async Task HeartbeatMustAttestTheExplicitConfiguredBoundary()
    {
        CreateSpool(lastSequence: 0);
        var launcherClient = new ObserverSpoolClient(root, Nonce,
            responseTimeout: TimeSpan.FromSeconds(1), pollInterval: TimeSpan.FromMilliseconds(5),
            expectedHealthBoundary: LauncherSingleplayerProofProvider.ObserverBoundary);

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => launcherClient.VerifyHeartbeatAsync(CancellationToken.None));

        Assert.Equal("observer-heartbeat-not-ready", failure.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "requests")));
    }

    [Fact]
    public async Task RejectsStaleAndWrongNonceHeartbeatsBeforePublishingRequest()
    {
        CreateSpool(lastSequence: 0, observedAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var stale = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => Client().HealthAsync(CancellationToken.None));
        Assert.Equal("observer-heartbeat-stale", stale.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "requests")));

        Directory.Delete(root, recursive: true);
        CreateSpool(lastSequence: 0, nonceHash: new string('0', 64));
        var mismatch = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => Client().HealthAsync(CancellationToken.None));
        Assert.Equal("observer-heartbeat-nonce-mismatch", mismatch.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "requests")));
    }

    [Fact]
    public async Task RejectsInvalidObservationEvidence()
    {
        CreateSpool(lastSequence: 9);
        var playerId = Guid.NewGuid();
        using var document = JsonDocument.Parse(ObservationPayload(playerId));
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(document.RootElement.GetRawText())!;
        values["evidenceValid"] = false;
        var client = Client();
        var server = RespondOnceAsync(JsonSerializer.Serialize(values), expectedSequence: 10, expectedPlayerId: playerId);

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => client.ObserveAsync(playerId, CancellationToken.None));
        await server;

        Assert.Equal("observer-evidence-invalid", failure.Code);
    }

    [Fact]
    public async Task TimesOutWithoutInventingASecondRequest()
    {
        CreateSpool(lastSequence: 6);
        var client = Client(responseTimeout: TimeSpan.FromMilliseconds(100));

        var failure = await Assert.ThrowsAsync<ObserverSpoolException>(
            () => client.HealthAsync(CancellationToken.None));

        Assert.Equal("observer-response-timeout", failure.Code);
        var request = Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "requests"), "*.request"));
        Assert.StartsWith("00000000000000000007-", Path.GetFileName(request), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadsCurrentUserProtectedSessionNonce()
    {
        Directory.CreateDirectory(root);
        var sessionId = Guid.NewGuid().ToString("D");
        var plain = Encoding.UTF8.GetBytes(Nonce);
        var entropy = Encoding.UTF8.GetBytes("HYTALE-QA-SESSION-v1");
        var encrypted = ProtectedData.Protect(plain, entropy, DataProtectionScope.CurrentUser);
        try
        {
            File.WriteAllText(Path.Combine(root, "session.json"), JsonSerializer.Serialize(new
            {
                sessionId,
                sessionNonceProtected = Convert.ToBase64String(encrypted),
                fixtureUuid = Guid.NewGuid(),
                fixtureName = "HYTALE_QA_BOT",
                endpoint = "127.0.0.1:5542",
                createdAtUtc = DateTimeOffset.UtcNow
            }));
            var paths = new QaPaths(root, root, root, root, root, root, root);
            var controller = new OfflineServerController(paths, new NeverRunner());

            var secret = controller.ReadSessionSecret(sessionId);

            Assert.Equal(Nonce, secret.SessionNonce);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    private ObserverSpoolClient Client(TimeSpan? responseTimeout = null) => new(
        root, Nonce, responseTimeout: responseTimeout ?? TimeSpan.FromSeconds(2),
        pollInterval: TimeSpan.FromMilliseconds(5));

    private void CreateSpool(long lastSequence, DateTimeOffset? observedAt = null, string? nonceHash = null)
    {
        Directory.CreateDirectory(Path.Combine(root, "requests"));
        Directory.CreateDirectory(Path.Combine(root, "responses"));
        Directory.CreateDirectory(Path.Combine(root, "events"));
        var time = observedAt ?? DateTimeOffset.UtcNow;
        var observedAtText = time.ToString("O");
        var actualNonceHash = nonceHash ?? Hash(Encoding.UTF8.GetBytes(Nonce));
        var canonical = new StringBuilder()
            .Append("HYTALE-QA-SPOOL-HEARTBEAT/1\n")
            .Append("ready=true\n")
            .Append("offline=true\n")
            .Append("effectiveAuthMode=OFFLINE\n")
            .Append("bridgeNetworkBoundary=DOCKER_SHARED_VOLUME_SPOOL\n")
            .Append("sessionNonceSha256=").Append(actualNonceHash).Append('\n')
            .Append("packetEvidenceValid=true\n")
            .Append("lastSequence=").Append(lastSequence).Append('\n')
            .Append("observedAt=").Append(observedAtText).Append('\n')
            .ToString();
        var heartbeat = JsonSerializer.Serialize(new
        {
            type = "spool-heartbeat",
            schemaVersion = 2,
            ready = true,
            offline = true,
            effectiveAuthMode = "OFFLINE",
            bridgeNetworkBoundary = "DOCKER_SHARED_VOLUME_SPOOL",
            sessionNonceSha256 = actualNonceHash,
            packetEvidenceValid = true,
            lastSequence,
            observedAt = observedAtText,
            mac = Mac(canonical)
        });
        var path = Path.Combine(root, "events", $"heartbeat-{time.ToUnixTimeMilliseconds():00000000000000000000}-{Guid.NewGuid():D}.json");
        File.WriteAllText(path, heartbeat);
    }

    private async Task RespondOnceAsync(string payload, long expectedSequence, Guid? expectedPlayerId = null,
        bool corruptHash = false, string? expectedCommand = null, bool corruptMac = false)
    {
        var requests = Path.Combine(root, "requests");
        string? path = null;
        for (var attempt = 0; attempt < 400 && path is null; attempt++)
        {
            path = Directory.EnumerateFiles(requests, "*.request").FirstOrDefault();
            if (path is null) await Task.Delay(5);
        }
        Assert.NotNull(path);
        var bytes = await File.ReadAllBytesAsync(path!);
        var content = Encoding.UTF8.GetString(bytes);
        var fields = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]);
        Assert.Equal(expectedSequence.ToString(), fields["sequence"]);
        if (expectedCommand is not null)
        {
            Assert.Equal(expectedCommand, fields["command"]);
            Assert.False(fields.ContainsKey("playerId"));
        }
        Assert.DoesNotContain("nonce=", content, StringComparison.OrdinalIgnoreCase);
        var macOffset = content.LastIndexOf("\nmac=", StringComparison.Ordinal);
        Assert.True(macOffset > 0);
        var unsigned = content[..(macOffset + 1)];
        using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Nonce)))
        {
            var expectedMac = Convert.ToHexString(
                hmac.ComputeHash(Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
            Assert.Equal(expectedMac, fields["mac"]);
        }
        if (expectedPlayerId.HasValue) Assert.Equal(expectedPlayerId.Value.ToString("D"), fields["playerId"]);
        var requestId = Guid.Parse(fields["requestId"]);
        var requestHash = corruptHash ? new string('f', 64) : Hash(bytes);
        var payloadHash = Hash(Encoding.UTF8.GetBytes(payload));
        var responseCanonical = new StringBuilder()
            .Append("HYTALE-QA-SPOOL-RESPONSE/1\n")
            .Append("requestId=").Append(requestId.ToString("D")).Append('\n')
            .Append("sequence=").Append(expectedSequence).Append('\n')
            .Append("requestSha256=").Append(requestHash).Append('\n')
            .Append("payloadSha256=").Append(payloadHash).Append('\n')
            .ToString();
        var metadata = JsonSerializer.Serialize(new
        {
            type = "spool-response",
            schemaVersion = 2,
            requestId,
            sequence = expectedSequence,
            requestSha256 = requestHash,
            payloadSha256 = payloadHash,
            mac = corruptMac ? new string('0', 64) : Mac(responseCanonical)
        });
        var destination = Path.Combine(root, "responses", $"{expectedSequence:00000000000000000000}-{requestId:D}.response.ndjson");
        var temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, metadata + "\n" + payload + "\n");
        File.Move(temporary, destination);
    }

    private static string HealthPayload() => JsonSerializer.Serialize(new
    {
        type = "health",
        schemaVersion = 1,
        ready = true,
        offline = true,
        effectiveAuthMode = "OFFLINE",
        bridgeNetworkBoundary = "DOCKER_SHARED_VOLUME_SPOOL",
        sessionNonceSha256 = Hash(Encoding.UTF8.GetBytes(Nonce)),
        packetEvidenceValid = true
    });

    private static string ObservationPayload(Guid playerId) => JsonSerializer.Serialize(new
    {
        type = "observation",
        schemaVersion = 1,
        sessionId = Guid.NewGuid(),
        observationSequence = 7,
        observedAt = DateTimeOffset.UtcNow,
        evidenceValid = true,
        packetBatch = new { schemaVersion = 1, valid = true, droppedFacts = 0, invalidReason = "", facts = Array.Empty<object>() },
        worldSnapshot = new { schemaVersion = 1, capturedAt = DateTimeOffset.UtcNow, playerId, worldId = Guid.NewGuid(), state = new { } }
    });

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Mac(string value)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Nonce));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class NeverRunner : IQaProcessRunner
    {
        public Task<QaProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
