using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using Hytale.Qa.Orchestrator;
using Hytale.Qa.Runner;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};
try
{
    var paths = QaPaths.Discover();
    var processes = new QaProcessRunner();
    var servers = new OfflineServerController(paths, processes);
    var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
    object result = command switch
    {
        "server-start" => await StartAsync(),
        "server-inspect" => await InspectAsync(),
        "observer-health" => await ObserverHealthAsync(),
        "observe" => await ObserveAsync(),
        "server-stop" => await StopAsync(),
        "scenarios-list" => new ScenarioCatalog(paths).List(),
        "scenarios-validate" => new ScenarioCatalog(paths).Validate(),
        "suites-list" => new SuiteCatalog(paths).List(),
        "suites-validate" => new SuiteCatalog(paths).Validate(),
        "profile-validate" => ValidateProfile(),
        "capabilities-audit" => new QaCapabilityAuditor(paths, new ScenarioCatalog(paths)).Audit(),
        "suite-plan" => PlanSuite(),
        "scenario-plan" => PlanScenario(),
        _ => new
        {
            usage = new[]
            {
                "server-start [session-guid]", "server-inspect [session-guid]",
                "observer-health [session-guid]", "observe <player-uuid> [session-guid]",
                "server-stop [session-guid]", "scenarios-list", "scenarios-validate",
                "suites-list", "suites-validate", "suite-plan <suite-id>",
                "scenario-plan <scenario-json>", "profile-validate", "capabilities-audit"
            }
        }
    };
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return 0;

    async Task<object> StartAsync()
    {
        var sessionId = args.ElementAtOrDefault(1) ?? Guid.NewGuid().ToString("D");
        var state = await servers.StartAsync(sessionId, CancellationToken.None);
        var observer = Observer(sessionId);
        var heartbeat = await observer.VerifyHeartbeatAsync(CancellationToken.None);
        var health = await observer.HealthAsync(CancellationToken.None);
        return new { state, heartbeat, health, inputArmed = false };
    }

    async Task<object> InspectAsync()
    {
        var sessionId = SessionId(args.ElementAtOrDefault(1));
        return await servers.InspectAsync(sessionId, true, CancellationToken.None);
    }

    async Task<object> ObserverHealthAsync()
    {
        var sessionId = SessionId(args.ElementAtOrDefault(1));
        var observer = Observer(sessionId);
        return new
        {
            heartbeat = await observer.VerifyHeartbeatAsync(CancellationToken.None),
            health = await observer.HealthAsync(CancellationToken.None)
        };
    }

    async Task<object> ObserveAsync()
    {
        if (!Guid.TryParse(args.ElementAtOrDefault(1), out var playerId) || playerId == Guid.Empty)
            throw new ArgumentException("observe requires a non-empty player UUID.");
        var sessionId = SessionId(args.ElementAtOrDefault(2));
        return await Observer(sessionId).ObserveAsync(playerId, CancellationToken.None);
    }

    async Task<object> StopAsync()
    {
        var sessionId = SessionId(args.ElementAtOrDefault(1));
        await servers.StopAsync(sessionId, CancellationToken.None);
        return new { sessionId, stopped = true, dataPreserved = true };
    }

    object PlanScenario()
    {
        var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("scenario-plan requires a JSON path.");
        var scenario = QaScenarioLoader.Load(Path.GetFullPath(path));
        return new
        {
            scenario.Id,
            scenario.Mode,
            capabilities = scenario.Capabilities.Order(StringComparer.Ordinal),
            stepCount = scenario.Steps.Count,
            operations = scenario.Steps.Select(step => new { step.Id, step.Operation, step.TimeoutSeconds }),
            executable = false,
            blocker = "A supported launcher-owned offline client lease must be armed before physical execution."
        };
    }

    object ValidateProfile()
    {
        var requiredDirectories = new[]
        {
            paths.ProjectRoot, paths.OfflineScriptsDirectory, paths.ScenarioDirectory, paths.SuiteDirectory
        };
        var requiredFiles = new[]
        {
            paths.ClientCapabilityPath, paths.ServerJarPath, paths.ClientExecutablePath,
            paths.FfmpegAllowlistPath, paths.ObserverCapabilitiesPath
        };
        var missingDirectories = requiredDirectories.Where(path => !Directory.Exists(path)).ToArray();
        var missingFiles = requiredFiles.Where(path => !File.Exists(path)).ToArray();
        var dockerIdentityValid = IsExplicitLoopbackEndpoint(paths.DockerEndpoint) &&
                                  !string.IsNullOrWhiteSpace(paths.DockerProject) &&
                                  !string.IsNullOrWhiteSpace(paths.DockerContainer) &&
                                  !string.IsNullOrWhiteSpace(paths.DockerNetwork);
        return new
        {
            schema = "hytale-qa-profile-validation-v1",
            paths.ProfileId,
            paths.ProjectRoot,
            paths.ToolRoot,
            paths.McpName,
            paths.McpTitle,
            paths.ScenarioDirectory,
            paths.SuiteDirectory,
            paths.EvidenceRootDirectory,
            paths.WorkerExecutablePath,
            docker = new { endpoint = paths.DockerEndpoint, project = paths.DockerProject,
                container = paths.DockerContainer, network = paths.DockerNetwork, valid = dockerIdentityValid },
            valid = missingDirectories.Length == 0 && missingFiles.Length == 0 && dockerIdentityValid,
            missingDirectories,
            missingFiles
        };
    }

    static bool IsExplicitLoopbackEndpoint(string value)
    {
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : $"udp://{value}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535 &&
               IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address);
    }

    object PlanSuite()
    {
        var id = args.ElementAtOrDefault(1) ?? throw new ArgumentException("suite-plan requires a suite id.");
        var suite = new SuiteCatalog(paths).Get(id);
        var scenarios = new ScenarioCatalog(paths).List().ToDictionary(value => value.Id, StringComparer.Ordinal);
        return new
        {
            suite.Id,
            suite.Purpose,
            suite.Coverage,
            maximumParallelClients = 1,
            scenarioCount = suite.ScenarioIds.Count,
            scenarios = suite.ScenarioIds.Select((scenarioId, index) => new
            {
                order = index + 1,
                scenarioId,
                mode = scenarios[scenarioId].Mode,
                path = scenarios[scenarioId].Path
            }),
            executable = false,
            blocker = "A supported launcher-owned offline client lease must be armed before physical execution."
        };
    }

    ObserverSpoolClient Observer(string sessionId)
    {
        var secret = servers.ReadSessionSecret(sessionId);
        return new(paths.ObserverSpoolDirectory, secret.SessionNonce);
    }

    string SessionId(string? supplied)
    {
        if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
        var secret = servers.ReadSessionSecretFromRecord();
        return secret.SessionId;
    }
}
catch (Exception failure)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false,
        error = failure is ObserverSpoolException observer ? observer.Code : failure.GetType().Name,
        message = failure.Message
    }));
    return 1;
}
