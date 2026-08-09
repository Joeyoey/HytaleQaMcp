using System.Security.Cryptography;
using System.Text.Json;

namespace Hytale.Qa.Orchestrator;

public sealed record OfflineServerState(
    string Schema,
    string SessionId,
    string Endpoint,
    string ProjectName,
    string ServiceName,
    string ContainerName,
    bool OfflineProven,
    bool ObserverReady,
    bool Armed,
    JsonElement Proof);

public sealed record OfflineSessionSecret(
    string SessionId,
    string SessionNonce,
    string FixtureUuid,
    string FixtureName,
    string Endpoint,
    DateTimeOffset CreatedAtUtc);

public sealed class OfflineServerController
{
    private static readonly byte[] SessionEntropy = System.Text.Encoding.UTF8.GetBytes("HYTALE-QA-SESSION-v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly QaPaths paths;
    private readonly IQaProcessRunner processes;

    public OfflineServerController(QaPaths paths, IQaProcessRunner processes)
    {
        this.paths = paths;
        this.processes = processes;
    }

    public async Task<OfflineServerState> StartAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out _)) throw new ArgumentException("Session id must be a GUID.", nameof(sessionId));
        var result = await RunPowerShellAsync(
            "Start-OfflineQaServer.ps1",
            ["-SessionId", sessionId],
            TimeSpan.FromMinutes(6),
            cancellationToken).ConfigureAwait(false);
        return ParseSuccessfulState(result, "offline server start");
    }

    public async Task<OfflineServerState> InspectAsync(string sessionId, bool requireArmed, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-SessionId", sessionId };
        if (requireArmed) arguments.Add("-RequireArmed");
        var result = await RunPowerShellAsync(
            "Get-OfflineQaServer.ps1", arguments, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        return ParseSuccessfulState(result, "offline server inspect");
    }

    public async Task StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var result = await RunPowerShellAsync(
            "Stop-OfflineQaServer.ps1",
            ["-SessionId", sessionId],
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Offline QA stop failed ({result.ExitCode}): {Sanitize(result.StandardError)}");
    }

    public OfflineSessionSecret ReadSessionSecret(string expectedSessionId)
    {
        var path = Path.Combine(paths.ControlDirectory, "session.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var sessionId = root.GetProperty("sessionId").GetString() ?? "";
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(sessionId),
                System.Text.Encoding.UTF8.GetBytes(expectedSessionId)))
            throw new InvalidOperationException("Offline session record does not match the requested session.");
        var sessionNonce = ReadSessionNonce(root);
        return new(
            sessionId,
            sessionNonce,
            root.GetProperty("fixtureUuid").GetString() ?? "",
            root.GetProperty("fixtureName").GetString() ?? "",
            root.GetProperty("endpoint").GetString() ?? "",
            DateTimeOffset.Parse(root.GetProperty("createdAtUtc").GetString() ?? ""));
    }

    public OfflineSessionSecret ReadSessionSecretFromRecord()
    {
        var path = Path.Combine(paths.ControlDirectory, "session.json");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var sessionId = document.RootElement.GetProperty("sessionId").GetString()
            ?? throw new InvalidDataException("Offline session record has no session id.");
        return ReadSessionSecret(sessionId);
    }

    private static string ReadSessionNonce(JsonElement root)
    {
        if (root.TryGetProperty("sessionNonceProtected", out var protectedProperty) &&
            protectedProperty.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(protectedProperty.GetString()))
        {
            byte[] protectedBytes;
            try
            {
                protectedBytes = Convert.FromBase64String(protectedProperty.GetString()!);
            }
            catch (FormatException failure)
            {
                throw new InvalidDataException("Protected session nonce is not valid base64.", failure);
            }
            try
            {
                var plain = ProtectedData.Unprotect(protectedBytes, SessionEntropy, DataProtectionScope.CurrentUser);
                try
                {
                    var nonce = System.Text.Encoding.UTF8.GetString(plain);
                    if (string.IsNullOrWhiteSpace(nonce)) throw new InvalidDataException("Protected session nonce is empty.");
                    return nonce;
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            catch (CryptographicException failure)
            {
                throw new InvalidDataException("Protected session nonce cannot be decrypted for the current user.", failure);
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        if (root.TryGetProperty("sessionNonce", out var legacyProperty) && legacyProperty.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(legacyProperty.GetString()))
            return legacyProperty.GetString()!;
        throw new InvalidDataException("Session nonce missing.");
    }

    internal static string ProtectSessionNonceForCurrentUser(string nonce)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(nonce);
        try
        {
            var protectedBytes = ProtectedData.Protect(plain, SessionEntropy, DataProtectionScope.CurrentUser);
            try { return Convert.ToBase64String(protectedBytes); }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private async Task<QaProcessResult> RunPowerShellAsync(
        string script,
        IReadOnlyList<string> scriptArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", Path.Combine(paths.OfflineScriptsDirectory, script)
        };
        arguments.AddRange(scriptArguments);
        return await processes.RunAsync(
            "powershell.exe", arguments, paths.ProjectRoot, timeout, cancellationToken).ConfigureAwait(false);
    }

    private static OfflineServerState ParseSuccessfulState(QaProcessResult result, string operation)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"{operation} failed ({result.ExitCode}): {Sanitize(result.StandardError)}");
        try
        {
            return JsonSerializer.Deserialize<OfflineServerState>(ExtractLastJsonObject(result.StandardOutput), JsonOptions)
                ?? throw new InvalidDataException($"{operation} returned no state.");
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException($"{operation} returned invalid JSON.", failure);
        }
    }

    internal static string ExtractLastJsonObject(string output)
    {
        var end = output.LastIndexOf('}');
        if (end < 0) throw new InvalidDataException("No JSON object found in process output.");
        var depth = 0;
        var inString = false;
        var escape = false;
        for (var index = end; index >= 0; index--)
        {
            var current = output[index];
            if (inString)
            {
                if (escape) escape = false;
                else if (current == '\\') escape = true;
                else if (current == '"') inString = false;
                continue;
            }
            if (current == '"') { inString = true; continue; }
            if (current == '}') depth++;
            else if (current == '{' && --depth == 0) return output[index..(end + 1)];
        }
        throw new InvalidDataException("Unbalanced JSON object in process output.");
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "no diagnostic output";
        var lines = value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" | ", lines.Take(8)).Replace("sessionNonce", "redacted", StringComparison.OrdinalIgnoreCase);
    }
}
