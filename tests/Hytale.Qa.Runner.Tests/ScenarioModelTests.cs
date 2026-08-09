using Hytale.Qa.Runner;

namespace Hytale.Qa.Runner.Tests;

public sealed class ScenarioModelTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-runner-model-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoaderPreservesDescriptionTagsAndCompleteFixture()
    {
        var path = Write("scenario.json", ScenarioJson(reordered: false));

        var scenario = QaScenarioLoader.Load(path);

        Assert.Equal("Full fixture proof", scenario.Description);
        Assert.Equal(["runner", "fixture"], scenario.Tags);
        Assert.Equal("hytale-qa-offline", scenario.Fixture.ServerProfile);
        Assert.Equal("fixture-v9", scenario.Fixture.Snapshot);
        Assert.Equal(1234567890123, scenario.Fixture.WorldSeed);
        Assert.Equal(987654321098, scenario.Fixture.RunSeed);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000777"), scenario.Fixture.Player.Uuid);
        Assert.Equal("HYTALE_QA_Fixture", scenario.Fixture.Player.Name);
        Assert.Equal(QaPlayerIdentitySelector.ExactFixture, scenario.Fixture.Player.IdentitySelector);
        Assert.Equal(1920, scenario.Fixture.Client.Width);
        Assert.Equal(1080, scenario.Fixture.Client.Height);
        Assert.Equal("borderless", scenario.Fixture.Client.DisplayMode);
        Assert.Equal(1.25, scenario.Fixture.Client.HudScale);
        Assert.Equal(85, scenario.Fixture.Client.Fov);
        Assert.Equal("keys-v2", scenario.Fixture.Client.KeybindProfile);
        Assert.Equal("graphics-v3", scenario.Fixture.Client.GraphicsProfile);
        Assert.Equal("loadout-v4", scenario.Fixture.Loadout);
        Assert.Equal(QaWorldSeedSelector.Exact, scenario.Fixture.WorldSeedSelector);
        Assert.Equal(QaObservedValueSelector.Exact, scenario.Fixture.RunSeedSelector);
        Assert.Equal(QaObservedValueSelector.Exact, scenario.Fixture.SnapshotSelector);
        Assert.Equal(QaObservedValueSelector.Exact, scenario.Fixture.LoadoutSelector);
        Assert.Equal(64, scenario.CanonicalSha256.Length);
        Assert.All(scenario.CanonicalSha256, character => Assert.True(Uri.IsHexDigit(character)));
    }

    [Fact]
    public void CanonicalHashIgnoresWhitespaceAndObjectPropertyOrder()
    {
        var first = QaScenarioLoader.Load(Write("first.json", ScenarioJson(reordered: false)));
        var second = QaScenarioLoader.Load(Write("second.json", ScenarioJson(reordered: true)));

        Assert.Equal(first.CanonicalSha256, second.CanonicalSha256);
    }

    [Fact]
    public void LoaderAcceptsProofBoundSolePlayerWithoutSyntheticIdentity()
    {
        var json = ScenarioJson(reordered: false).Replace(
            "\"player\": {\"uuid\": \"00000000-0000-0000-0000-000000000777\", \"name\": \"HYTALE_QA_Fixture\"}",
            "\"player\": {\"identitySelector\": \"proof_bound_sole_player\"}",
            StringComparison.Ordinal);

        var scenario = QaScenarioLoader.Load(Write("proof-bound.json", json));

        Assert.Equal(QaPlayerIdentitySelector.ProofBoundSolePlayer, scenario.Fixture.Player.IdentitySelector);
        Assert.Null(scenario.Fixture.Player.Uuid);
        Assert.Null(scenario.Fixture.Player.Name);
    }

    [Fact]
    public void LoaderRejectsSyntheticIdentityOnProofBoundSelector()
    {
        var json = ScenarioJson(reordered: false).Replace(
            "\"player\": {\"uuid\": \"00000000-0000-0000-0000-000000000777\", \"name\": \"HYTALE_QA_Fixture\"}",
            "\"player\": {\"identitySelector\": \"proof_bound_sole_player\", \"uuid\": \"00000000-0000-0000-0000-000000000777\", \"name\": \"HYTALE_QA_Fixture\"}",
            StringComparison.Ordinal);

        var failure = Assert.Throws<InvalidDataException>(() =>
            QaScenarioLoader.Load(Write("proof-bound-synthetic.json", json)));

        Assert.Equal("scenario-proof-bound-player-cannot-declare-synthetic-identity", failure.Message);
    }

    [Fact]
    public void LoaderSupportsProofBoundWorldAndObserveAndPinPoliciesWithoutSyntheticValues()
    {
        var json = ScenarioJson(reordered: false)
            .Replace("\"snapshot\": \"fixture-v9\",", "\"snapshotSelector\": \"observe_and_pin\",", StringComparison.Ordinal)
            .Replace("\"worldSeed\": 1234567890123,", "\"worldSeedSelector\": \"proof_bound\",", StringComparison.Ordinal)
            .Replace("\"runSeed\": 987654321098,", "\"runSeedSelector\": \"observe_and_pin\",", StringComparison.Ordinal)
            .Replace("\"loadout\": \"loadout-v4\"", "\"loadoutSelector\": \"observe_and_pin\"", StringComparison.Ordinal);

        var scenario = QaScenarioLoader.Load(Write("proof-bound-world.json", json));

        Assert.Null(scenario.Fixture.WorldSeed);
        Assert.Null(scenario.Fixture.RunSeed);
        Assert.Null(scenario.Fixture.Snapshot);
        Assert.Null(scenario.Fixture.Loadout);
        Assert.Equal(QaWorldSeedSelector.ProofBound, scenario.Fixture.WorldSeedSelector);
        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.RunSeedSelector);
        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.SnapshotSelector);
        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.LoadoutSelector);
    }

    [Fact]
    public void OmittedValuesAndExplicitSelectorUseObserveAndPin()
    {
        var json = ScenarioJson(reordered: false)
            .Replace("\"snapshot\": \"fixture-v9\",", "", StringComparison.Ordinal)
            .Replace("\"runSeed\": 987654321098,", "", StringComparison.Ordinal)
            .Replace("\"loadout\": \"loadout-v4\"", "\"loadoutSelector\": \"observe_and_pin\"", StringComparison.Ordinal);

        var scenario = QaScenarioLoader.Load(Write("implicit-observe-pin.json", json));

        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.RunSeedSelector);
        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.SnapshotSelector);
        Assert.Equal(QaObservedValueSelector.ObserveAndPin, scenario.Fixture.LoadoutSelector);
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ScenarioJson(bool reordered) => reordered
        ? """
          {
            "artifacts":{"manifestHash":true,"stateDiffs":true,"packetEvidence":true,"inputTranscript":true,"rollingVideoSeconds":0,"screenshots":"failures"},
            "steps":[{"operation":"session.start","id":"start"},{"operation":"session.stop","id":"stop"}],
            "budgets":{"maximumRecoveries":2,"maximumDeaths":1,"noProgressSeconds":7,"stepSeconds":30,"totalSeconds":60},
            "safety":{"abortOnEndpointDrift":true,"abortOnFocusLoss":true,"maximumClientProcesses":1,"loopbackOnly":true,"offlineRequired":true,"forbiddenCapabilities":[]},
            "fixture":{"loadout":"loadout-v4","client":{"graphicsProfile":"graphics-v3","keybindProfile":"keys-v2","fov":85,"hudScale":1.25,"displayMode":"borderless","height":1080,"width":1920},"player":{"name":"HYTALE_QA_Fixture","uuid":"00000000-0000-0000-0000-000000000777"},"runSeed":987654321098,"worldSeed":1234567890123,"snapshot":"fixture-v9","serverProfile":"hytale-qa-offline"},
            "capabilities":["physical_input"],
            "mode":"guided_physical",
            "tags":["runner","fixture"],
            "description":"Full fixture proof",
            "id":"test.full-fixture",
            "schema":"hytale-qa/v1"
          }
          """
        : """
          {
            "schema": "hytale-qa/v1",
            "id": "test.full-fixture",
            "description": "Full fixture proof",
            "tags": ["runner", "fixture"],
            "mode": "guided_physical",
            "capabilities": ["physical_input"],
            "fixture": {
              "serverProfile": "hytale-qa-offline",
              "snapshot": "fixture-v9",
              "worldSeed": 1234567890123,
              "runSeed": 987654321098,
              "player": {"uuid": "00000000-0000-0000-0000-000000000777", "name": "HYTALE_QA_Fixture"},
              "client": {"width": 1920, "height": 1080, "displayMode": "borderless", "hudScale": 1.25, "fov": 85, "keybindProfile": "keys-v2", "graphicsProfile": "graphics-v3"},
              "loadout": "loadout-v4"
            },
            "safety": {"offlineRequired": true, "loopbackOnly": true, "maximumClientProcesses": 1, "abortOnFocusLoss": true, "abortOnEndpointDrift": true, "forbiddenCapabilities": []},
            "budgets": {"totalSeconds": 60, "stepSeconds": 30, "noProgressSeconds": 7, "maximumDeaths": 1, "maximumRecoveries": 2},
            "steps": [{"id": "start", "operation": "session.start"}, {"id": "stop", "operation": "session.stop"}],
            "artifacts": {"screenshots": "failures", "rollingVideoSeconds": 0, "inputTranscript": true, "packetEvidence": true, "stateDiffs": true, "manifestHash": true}
          }
          """;

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
