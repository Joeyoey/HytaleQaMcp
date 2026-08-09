using System.Text.Json;

namespace Hytale.Qa.Orchestrator;

public sealed record QaPaths(
    string ProjectRoot,
    string OfflineScriptsDirectory,
    string ScenarioDirectory,
    string ControlDirectory,
    string ClientCapabilityPath,
    string ServerJarPath,
    string ClientExecutablePath)
{
    public string ProfileId { get; init; } = "legacy";
    public string ToolRoot { get; init; } = "";
    public string? SuiteDirectoryOverride { get; init; }
    public string? EvidenceRootDirectoryOverride { get; init; }
    public string? LauncherControlDirectoryOverride { get; init; }
    public string? FfmpegAllowlistPathOverride { get; init; }
    public string? WorkerExecutablePathOverride { get; init; }
    public string? ObserverCapabilitiesPathOverride { get; init; }
    public string McpName { get; init; } = "hytale-qa";
    public string McpTitle { get; init; } = "Offline Hytale QA";
    public string ServerProfileId { get; init; } = "hytale-qa-offline";
    public string DockerEndpoint { get; init; } = "127.0.0.1:5542";
    public string DockerProject { get; init; } = "hytale-qa-offline";
    public string DockerContainer { get; init; } = "hytale-qa-offline";
    public string DockerNetwork { get; init; } = "hytale-qa-offline-net";

    public string ObserverSpoolDirectory => Path.Combine(ControlDirectory, "spool");
    public string SuiteDirectory => SuiteDirectoryOverride ?? Path.Combine(ProjectRoot, "qa-automation", "suites");
    public string EvidenceRootDirectory => EvidenceRootDirectoryOverride ?? Path.Combine(ProjectRoot, "run", "qa-evidence");
    public string ArtifactDirectory => Path.Combine(EvidenceRootDirectory, "artifacts");
    public string LauncherControlDirectory => LauncherControlDirectoryOverride ??
        Path.Combine(ProjectRoot, "run", "qa-offline", "launcher-singleplayer-control");
    public string LauncherObserverSpoolDirectory => Path.Combine(LauncherControlDirectory, "spool");
    public string LauncherProofDirectory => Path.Combine(LauncherControlDirectory, "proofs");
    public string RunDirectory => Path.Combine(EvidenceRootDirectory, "runs");
    public string FfmpegAllowlistPath => FfmpegAllowlistPathOverride ??
        Path.Combine(EffectiveToolRoot, "config", "ffmpeg.allowlist.local.json");
    public string ObserverCapabilitiesPath => ObserverCapabilitiesPathOverride ??
        Path.Combine(ProjectRoot, "qa-automation", "observer-capabilities.json");
    private string EffectiveToolRoot => string.IsNullOrWhiteSpace(ToolRoot)
        ? Path.Combine(ProjectRoot, "tools", "hytale-qa")
        : ToolRoot;

    public string SessionArtifactDirectory(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var parsed) || parsed == Guid.Empty)
            throw new ArgumentException("Session artifact directory requires a non-empty UUID.", nameof(sessionId));
        return Path.Combine(ArtifactDirectory, parsed.ToString("D"));
    }

    public string RequireSafeEvidencePath(string path)
    {
        var root = Path.GetFullPath(EvidenceRootDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new InvalidDataException("Evidence path escaped the persistent evidence root.");
        var cursor = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
        while (cursor is not null && cursor.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Evidence path contains a reparse/junction ancestor: {cursor}");
            if (string.Equals(cursor.TrimEnd(Path.DirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase)) break;
            cursor = Path.GetDirectoryName(cursor);
        }
        return full;
    }

    public void RequireSafeEvidenceTree(string path)
    {
        var root = RequireSafeEvidencePath(path);
        if (!Directory.Exists(root)) return;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Evidence tree contains a reparse/junction entry: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    public string WorkerExecutablePath
    {
        get
        {
            var configured = FirstEnvironment("HYTALE_QA_WORKER_PATH");
            if (string.IsNullOrWhiteSpace(configured)) configured = WorkerExecutablePathOverride;
            return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(EffectiveToolRoot, "src", "Hytale.Qa.Worker", "bin", "Release",
                    "net9.0-windows10.0.20348.0", "Hytale.Qa.Worker.exe")
                : configured);
        }
    }

    public static QaPaths Discover(string? startDirectory = null)
    {
        var start = Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory);
        var profilePath = FirstEnvironment("HYTALE_QA_PROFILE");
        if (string.IsNullOrWhiteSpace(profilePath)) profilePath = FindProfile(start);
        if (!string.IsNullOrWhiteSpace(profilePath)) return FromProfile(Path.GetFullPath(profilePath));
        throw new DirectoryNotFoundException(
            "Could not locate a Hytale QA profile. Set HYTALE_QA_PROFILE or add .hytale-qa/profile.json.");
    }

    public static QaPaths FromProfile(string profilePath)
    {
        if (!File.Exists(profilePath)) throw new FileNotFoundException("Hytale QA profile was not found.", profilePath);
        using var document = JsonDocument.Parse(File.ReadAllBytes(profilePath));
        var root = document.RootElement;
        if (!string.Equals(Required(root, "schema"), "hytale-qa-profile-v1", StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported Hytale QA profile schema.");
        var profileDirectory = Path.GetDirectoryName(Path.GetFullPath(profilePath))!;
        var toolRoot = FirstEnvironment("HYTALE_QA_HOME") ?? FindToolRoot(AppContext.BaseDirectory) ??
            FindToolRoot(Environment.CurrentDirectory) ?? throw new DirectoryNotFoundException(
                "Could not locate Hytale QA tool root. Set HYTALE_QA_HOME.");
        var hytaleLatest = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Hytale", "install", "release", "package", "game", "latest");
        var projectRoot = Resolve(Required(root, "projectRoot"), profileDirectory,
            profileDirectory, toolRoot, hytaleLatest);
        string PathValue(string name) => Resolve(Required(root, name), profileDirectory,
            projectRoot, toolRoot, hytaleLatest);
        var mcp = root.TryGetProperty("mcp", out var mcpValue) && mcpValue.ValueKind == JsonValueKind.Object
            ? mcpValue : default;
        var docker = root.TryGetProperty("docker", out var dockerValue) && dockerValue.ValueKind == JsonValueKind.Object
            ? dockerValue : default;
        return new QaPaths(
            projectRoot,
            PathValue("offlineScriptsDirectory"),
            PathValue("scenarioDirectory"),
            PathValue("controlDirectory"),
            PathValue("clientCapabilityPath"),
            PathValue("serverJarPath"),
            PathValue("clientExecutablePath"))
        {
            ProfileId = Required(root, "id"),
            ToolRoot = Path.GetFullPath(toolRoot),
            SuiteDirectoryOverride = PathValue("suiteDirectory"),
            EvidenceRootDirectoryOverride = PathValue("evidenceRootDirectory"),
            LauncherControlDirectoryOverride = PathValue("launcherControlDirectory"),
            FfmpegAllowlistPathOverride = PathValue("ffmpegAllowlistPath"),
            WorkerExecutablePathOverride = PathValue("workerExecutablePath"),
            ObserverCapabilitiesPathOverride = PathValue("observerCapabilitiesPath"),
            McpName = Optional(mcp, "name") ?? "hytale-qa",
            McpTitle = Optional(mcp, "title") ?? "Offline Hytale QA",
            ServerProfileId = Optional(root, "serverProfile") ?? Required(root, "id") + "-offline",
            DockerEndpoint = Required(docker, "endpoint"),
            DockerProject = Required(docker, "project"),
            DockerContainer = Required(docker, "container"),
            DockerNetwork = Required(docker, "network")
        };
    }

    private static string? FindProfile(string start)
    {
        var cursor = new DirectoryInfo(start);
        while (cursor is not null)
        {
            foreach (var candidate in new[] { Path.Combine(cursor.FullName, ".hytale-qa", "profile.json"),
                         Path.Combine(cursor.FullName, "hytale-qa.profile.json") })
                if (File.Exists(candidate)) return candidate;
            cursor = cursor.Parent;
        }
        return null;
    }

    private static string? FindToolRoot(string start)
    {
        var cursor = new DirectoryInfo(Path.GetFullPath(start));
        while (cursor is not null)
        {
            if (File.Exists(Path.Combine(cursor.FullName, "Hytale.Qa.sln"))) return cursor.FullName;
            cursor = cursor.Parent;
        }
        return null;
    }

    private static string Resolve(string value, string profileDirectory, string projectRoot,
        string toolRoot, string hytaleLatest)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value)
            .Replace("{profileDir}", profileDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{projectRoot}", projectRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{toolRoot}", toolRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("{hytaleLatest}", hytaleLatest, StringComparison.OrdinalIgnoreCase)
            .Replace("{appData}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                StringComparison.OrdinalIgnoreCase)
            .Replace("{localAppData}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(profileDirectory, expanded));
    }

    private static string Required(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new InvalidDataException($"Hytale QA profile requires non-empty '{name}'.");
    private static string? Optional(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? FirstEnvironment(params string[] names) => names.Select(Environment.GetEnvironmentVariable)
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
