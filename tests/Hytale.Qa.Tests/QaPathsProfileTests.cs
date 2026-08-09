using System.Text.Json;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class QaPathsProfileTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-profile-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProfileResolvesRelativeAndPortableTokensWithoutMachineSpecificUserPaths()
    {
        var profileDirectory = Path.Combine(root, ".hytale-qa");
        Directory.CreateDirectory(profileDirectory);
        var profile = Path.Combine(profileDirectory, "profile.json");
        File.WriteAllText(profile, JsonSerializer.Serialize(new
        {
            schema = "hytale-qa-profile-v1",
            id = "portable-test",
            projectRoot = "..",
            offlineScriptsDirectory = "{projectRoot}/scripts/qa/offline",
            scenarioDirectory = "{projectRoot}/qa-automation/scenarios",
            suiteDirectory = "{projectRoot}/qa-automation/suites",
            controlDirectory = "{projectRoot}/run/control",
            evidenceRootDirectory = "{projectRoot}/run/evidence",
            launcherControlDirectory = "{projectRoot}/run/launcher",
            clientCapabilityPath = "{projectRoot}/client.json",
            serverJarPath = "{hytaleLatest}/Server/HytaleServer.jar",
            clientExecutablePath = "{hytaleLatest}/Client/HytaleClient.exe",
            workerExecutablePath = "{toolRoot}/worker.exe",
            ffmpegAllowlistPath = "{toolRoot}/config/ffmpeg.allowlist.local.json",
            observerCapabilitiesPath = "{projectRoot}/qa-automation/observer-capabilities.json",
            docker = new { endpoint = "127.0.0.1:5656", project = "portable-project",
                container = "portable-container", network = "portable-network" },
            mcp = new { name = "portable-qa", title = "Portable QA" }
        }));

        var paths = QaPaths.FromProfile(profile);

        Assert.Equal("portable-test", paths.ProfileId);
        Assert.Equal(Path.GetFullPath(root), paths.ProjectRoot);
        Assert.StartsWith(paths.ProjectRoot, paths.ScenarioDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(paths.ToolRoot, paths.WorkerExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("game", "latest", "Client", "HytaleClient.exe"),
            paths.ClientExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("portable-qa", paths.McpName);
        Assert.Equal("127.0.0.1:5656", paths.DockerEndpoint);
        Assert.Equal("portable-project", paths.DockerProject);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
