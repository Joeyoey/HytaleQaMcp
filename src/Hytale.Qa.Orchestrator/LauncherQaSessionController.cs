using System.Text.Json;

namespace Hytale.Qa.Orchestrator;

/// <summary>
/// Invokes the fail-closed launcher-owned singleplayer lifecycle. Bootstrap
/// creates one visible official launcher with a process-tree-scoped QA
/// environment; only Arm can attest that its actual server is OFFLINE.
/// </summary>
public sealed class LauncherQaSessionController(QaPaths paths, IQaProcessRunner processes)
{
    public Task<JsonElement> BootstrapAsync(string? sessionId, CancellationToken cancellationToken) =>
        RunAsync("Start-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromMinutes(2), cancellationToken);

    public Task<JsonElement> ArmAsync(string sessionId, CancellationToken cancellationToken) =>
        RunAsync("Arm-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromMinutes(2), cancellationToken);

    public Task<JsonElement> StateAsync(string sessionId, CancellationToken cancellationToken) =>
        RunAsync("Get-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<JsonElement> VerifyAsync(string sessionId, CancellationToken cancellationToken) =>
        RunAsync("Verify-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<JsonElement> StopAsync(string sessionId, CancellationToken cancellationToken) =>
        RunAsync("Stop-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromSeconds(30), cancellationToken);

    public Task<JsonElement> ClearAsync(string sessionId, CancellationToken cancellationToken) =>
        RunAsync("Clear-LauncherOfflineQaSession.ps1", SessionArguments(sessionId),
            TimeSpan.FromSeconds(30), cancellationToken);

    private async Task<JsonElement> RunAsync(
        string script,
        IReadOnlyList<string> scriptArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var scriptPath = Path.GetFullPath(Path.Combine(paths.OfflineScriptsDirectory, script));
        var scriptRoot = Path.GetFullPath(paths.OfflineScriptsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!scriptPath.StartsWith(scriptRoot, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(scriptPath) ||
            (File.GetAttributes(scriptPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Launcher lifecycle script is missing or unsafe.");

        var arguments = new List<string>
        {
            "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", scriptPath
        };
        arguments.AddRange(scriptArguments);
        var result = await processes.RunAsync(
            "powershell.exe", arguments, paths.ProjectRoot, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Launcher QA lifecycle '{script}' failed ({result.ExitCode}): {Sanitize(result.StandardError)}");
        try
        {
            using var document = JsonDocument.Parse(ExtractLastJsonObject(result.StandardOutput));
            return document.RootElement.Clone();
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException(
                $"Launcher QA lifecycle '{script}' returned invalid JSON.", failure);
        }
    }

    private static IReadOnlyList<string> SessionArguments(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return [];
        if (!Guid.TryParseExact(sessionId, "D", out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Session id must be a non-empty canonical UUID.", nameof(sessionId));
        return ["-SessionId", parsed.ToString("D")];
    }

    private static string ExtractLastJsonObject(string output)
    {
        var start = output.LastIndexOf('\n');
        while (start >= 0)
        {
            var candidate = output[(start + 1)..].Trim();
            if (candidate.StartsWith('{'))
            {
                try
                {
                    using var parsed = JsonDocument.Parse(candidate);
                    return candidate;
                }
                catch (JsonException) { }
            }
            start = start == 0 ? -1 : output.LastIndexOf('\n', start - 1);
        }
        var whole = output.Trim();
        if (whole.StartsWith('{')) return whole;
        throw new InvalidDataException("No JSON object was returned by launcher lifecycle script.");
    }

    private static string Sanitize(string value)
    {
        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 2048 ? normalized : normalized[..2048];
    }
}
