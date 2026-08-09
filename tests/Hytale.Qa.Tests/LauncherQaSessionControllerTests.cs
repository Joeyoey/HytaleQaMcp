using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class LauncherQaSessionControllerTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "hytale-qa-launcher-controller-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BootstrapUsesOnlyTheAllowlistedLifecycleScriptAndLeavesProofFalse()
    {
        var paths = Paths();
        var script = Path.Combine(paths.OfflineScriptsDirectory, "Start-LauncherOfflineQaSession.ps1");
        File.WriteAllText(script, "# test");
        var runner = new RecordingRunner("""
            warning text
            {
              "schema": "hytale-qa-launcher-qa-bootstrap-v1",
              "offlineProven": false,
              "armed": false
            }
            """);
        var controller = new LauncherQaSessionController(paths, runner);

        var result = await controller.BootstrapAsync(null, CancellationToken.None);

        Assert.False(result.GetProperty("offlineProven").GetBoolean());
        Assert.False(result.GetProperty("armed").GetBoolean());
        Assert.Single(runner.Calls);
        Assert.Contains(script, runner.Calls[0].Arguments);
        Assert.DoesNotContain("-SessionId", runner.Calls[0].Arguments);
    }

    [Fact]
    public async Task ArmPassesOnlyCanonicalSessionIdentityToExactScript()
    {
        var paths = Paths();
        var script = Path.Combine(paths.OfflineScriptsDirectory, "Arm-LauncherOfflineQaSession.ps1");
        File.WriteAllText(script, "# test");
        var runner = new RecordingRunner("{\"armed\":true,\"offlineSingleplayerProof\":true}");
        var controller = new LauncherQaSessionController(paths, runner);
        var sessionId = Guid.NewGuid().ToString("D");

        var result = await controller.ArmAsync(sessionId, CancellationToken.None);

        Assert.True(result.GetProperty("armed").GetBoolean());
        Assert.Equal(sessionId, runner.Calls[0].Arguments[^1]);
        Assert.Equal("-SessionId", runner.Calls[0].Arguments[^2]);
    }

    [Fact]
    public async Task InvalidSessionIdentityNeverStartsPowerShell()
    {
        var paths = Paths();
        var runner = new RecordingRunner("{}");
        var controller = new LauncherQaSessionController(paths, runner);

        await Assert.ThrowsAsync<ArgumentException>(
            () => controller.ArmAsync("../wrong", CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task ReparseOrMissingScriptFailsBeforePowerShell()
    {
        var paths = Paths();
        var runner = new RecordingRunner("{}");
        var controller = new LauncherQaSessionController(paths, runner);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.VerifyAsync(Guid.NewGuid().ToString("D"), CancellationToken.None));

        Assert.Empty(runner.Calls);
    }

    private QaPaths Paths()
    {
        var scripts = Path.Combine(root, "scripts");
        Directory.CreateDirectory(scripts);
        return new(root, scripts, Path.Combine(root, "scenarios"),
            Path.Combine(root, "control"), Path.Combine(root, "capability.json"),
            Path.Combine(root, "server.jar"), Path.Combine(root, "HytaleClient.exe"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingRunner(string output) : IQaProcessRunner
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = [];

        public Task<QaProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add((executable, arguments.ToArray()));
            return Task.FromResult(new QaProcessResult(0, output, ""));
        }
    }
}
