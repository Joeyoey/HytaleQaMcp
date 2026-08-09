using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Mcp.Tests;

public sealed class McpProtocolTests
{
    [Fact]
    public async Task OfficialClientNegotiatesListsAndCallsReadOnlyTool()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && !File.Exists(Path.Combine(cursor.FullName, "Hytale.Qa.sln"))) cursor = cursor.Parent;
        var toolRoot = cursor?.FullName ?? throw new DirectoryNotFoundException("Could not locate QA tool root.");
        var profile = Path.Combine(toolRoot, "examples", "basic-project", ".hytale-qa", "profile.json");
        var paths = QaPaths.FromProfile(profile);
        var server = Path.Combine(toolRoot, "src", "Hytale.Qa.Mcp",
            "bin", "Release", "net9.0-windows10.0.20348.0", "Hytale.Qa.Mcp.dll");
        Assert.True(File.Exists(server), $"MCP server build is missing: {server}");
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["HYTALE_QA_HOME"] = toolRoot;
        environment["HYTALE_QA_PROFILE"] = profile;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "sample-hytale-qa-test",
            Command = "dotnet",
            Arguments = [server],
            WorkingDirectory = paths.ProjectRoot,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(5)
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Contains(tools, tool => tool.Name == "qa_session_start");
        Assert.Contains(tools, tool => tool.Name == "qa_observe_player");
        Assert.Contains(tools, tool => tool.Name == "qa_observe_sole_player");
        Assert.Contains(tools, tool => tool.Name == "qa_scenarios_validate");
        Assert.Contains(tools, tool => tool.Name == "qa_suites_validate");
        Assert.Contains(tools, tool => tool.Name == "qa_capabilities_audit");
        Assert.Contains(tools, tool => tool.Name == "qa_scenario_run");
        Assert.Contains(tools, tool => tool.Name == "qa_suite_run");
        Assert.Contains(tools, tool => tool.Name == "qa_suite_handoff" &&
            tool.Description!.Contains("does not launch", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, tool => tool.Name == "qa_run_handoff" &&
            tool.Description!.Contains("same one-client PID", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, tool => tool.Name == "qa_navigate_observed");
        Assert.Contains(tools, tool => tool.Name == "qa_combat_observed_current_run");
        Assert.Contains(tools, tool => tool.Name == "qa_launcher_offline_bootstrap");
        Assert.Contains(tools, tool => tool.Name == "qa_launcher_offline_arm");
        Assert.Contains(tools, tool => tool.Name == "qa_launcher_offline_stop" &&
            tool.Description!.Contains("does not terminate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(tools, tool => tool.Name == "qa_rolling_start");
        Assert.Contains(tools, tool => tool.Name == "qa_rolling_abort");
        Assert.DoesNotContain(tools, tool => tool.Name == "qa_client_attach");
        Assert.DoesNotContain(tools, tool => tool.Name == "qa_navigate_step");
        Assert.DoesNotContain(tools, tool => tool.Name == "qa_aim_step");
        Assert.DoesNotContain(tools, tool => tool.Name == "qa_combat_step");

        var result = await client.CallToolAsync("qa_scenarios_validate",
            new Dictionary<string, object?>(), cancellationToken: timeout.Token);
        var content = Assert.Single(result.Content.OfType<TextContentBlock>());
        Assert.Contains("\"valid\": true", content.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"scenarioCount\": 1", content.Text, StringComparison.OrdinalIgnoreCase);
    }
}
