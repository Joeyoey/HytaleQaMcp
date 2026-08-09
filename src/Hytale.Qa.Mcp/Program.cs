using Hytale.Qa.Orchestrator;
using Hytale.Qa.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
var paths = QaPaths.Discover();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<IQaProcessRunner, QaProcessRunner>();
builder.Services.AddSingleton<OfflineServerController>();
builder.Services.AddSingleton<LauncherQaSessionController>();
builder.Services.AddSingleton<IObserverSpoolClientFactory, ObserverSpoolClientFactory>();
builder.Services.AddSingleton<IWorkerRpcClientFactory, WorkerRpcProcessClientFactory>();
builder.Services.AddSingleton<ILauncherProcessInspector, WindowsLauncherProcessInspector>();
builder.Services.AddSingleton<DockerWorkerOfflineProofProviderFactory>();
builder.Services.AddSingleton<LauncherSingleplayerProofProviderFactory>();
builder.Services.AddSingleton<ILauncherProofValidator>(services => services.GetRequiredService<LauncherSingleplayerProofProviderFactory>());
builder.Services.AddSingleton<IWorkerOfflineProofProviderFactory, WorkerOfflineProofProviderFactory>();
builder.Services.AddSingleton<IWorkerControlService, WorkerControlService>();
builder.Services.AddSingleton<AuthenticatedObserverControls>();
builder.Services.AddSingleton<IHytaleScenarioRuntimeFactory, HytaleScenarioRuntimeFactory>();
builder.Services.AddSingleton<QaSessionOrchestrator>();
builder.Services.AddSingleton<ScenarioCatalog>();
builder.Services.AddSingleton<SuiteCatalog>();
builder.Services.AddSingleton<QaCapabilityAuditor>();
builder.Services.AddSingleton<QaRunCoordinator>();
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = paths.McpName,
            Title = paths.McpTitle,
            Version = "1.0.0",
            Description = "Fail-closed, offline-only Hytale client automation and evidence collection."
        };
        options.ServerInstructions =
            "OFFLINE-ONLY Hytale QA. Never use these tools against authenticated/live servers or more than one client. " +
            "Begin with state/proof tools. Input and capture require exact launcher-owned OFFLINE proof, one pinned " +
            "HytaleClient PID, and a fresh authenticated observer; loss of any predicate aborts. Never handle OAuth, " +
            "authentication UI, or UAC. Use observer-derived semantic actions only. Preserve Blocked and Untested " +
            "outcomes; never infer Pass from a fresh snapshot, a screenshot alone, or an absent enemy.";
    })
    .WithStdioServerTransport()
    .WithTools<QaMcpTools>();
await builder.Build().RunAsync();
