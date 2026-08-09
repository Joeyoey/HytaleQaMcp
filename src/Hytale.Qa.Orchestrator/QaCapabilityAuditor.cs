using Hytale.Qa.Runner;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hytale.Qa.Orchestrator;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QaAutomationLevel
{
    Physical,
    AuthenticatedReadOnly,
    CaptureOnly,
    Unsupported
}

public sealed record QaOperationCapability(string Operation, QaAutomationLevel Level, string Requirement);
public sealed record QaOperationCoverage(string Operation, QaAutomationLevel Level, int StepCount,
    int SupportedStepCount, int ScenarioCount, string Requirement);
public sealed record QaCapabilityAudit(string Schema, string ProfileId, int ScenarioCount, int DistinctOperationCount,
    int AdapterOperationCount, int UnsupportedOperationCount, int FullyAdapterBackedScenarioCount,
    IReadOnlyList<QaOperationCoverage> Operations, IReadOnlyList<string> MissingObserverContracts,
    IReadOnlyList<string> UnsupportedOperations);

public sealed class QaCapabilityAuditor(QaPaths paths, ScenarioCatalog scenarios)
{
    private static readonly IReadOnlyDictionary<string, QaOperationCapability> Capabilities =
        new QaOperationCapability[]
        {
            Physical("session.start", "Launcher-owned OFFLINE proof and fixture provenance"),
            Physical("client.launch_offline", "One pinned client and gameplay-surface classifier"),
            Physical("session.stop", "Input/media release and worker detach"),
            Physical("wait.for", "Authenticated objective/world state"),
            Physical("navigate.to", "Server-authored semantic target coordinates"),
            Physical("interact.with", "Server-authored target and authoritative state transition"),
            Physical("route.choose", "Server-authored route target and selectedRoute state"),
            Physical("combat.clear", "Authenticated current-run hostile lifecycle"),
            Physical("combat.use_ability", "Authenticated target and combat action counter"),
            Physical("trace.mark", "Proof-gated look/click trace probe"),
            ReadOnly("assert.state", "Authenticated observer state"),
            ReadOnly("assert.visual", "Objective bitmap classifier for declared fields"),
            ReadOnly("inventory.inspect", "nativeInventory.items with exact semantic selector"),
            ReadOnly("assert.count", "qaEvidence.events with event attributes"),
            ReadOnly("assert.correlated", "qaEvidence.events with epoch-millisecond timestamps"),
            ReadOnly("assert.hash_chain", "qaEvidence.hashChains keyed by subject"),
            Capture("assert.audio", "Process WAV capture exists; an objective cue classifier is still required"),
            Unsupported("assert.asset", "Model/texture/icon inventory plus image classifier"),
            Unsupported("assert.subtitle", "Semantic subtitle surface or OCR classifier"),
            ReadOnly("assert.ui", "semanticUi.assertions authenticated by the observer"),
            Physical("ui.activate", "Authenticated semanticUi node, client-contained pointer, and revision evidence"),
            Physical("inventory.equip", "Semantic item selection, physical page action, and post-mutation evidence"),
            Physical("inventory.move", "Semantic item selection, physical page action, and post-mutation evidence"),
            Physical("inventory.reroll", "Physical preview/commit sequence plus exact signed-stack/WAL evidence"),
            Physical("inventory.salvage", "Physical confirmation/commit sequence plus exact signed-stack/WAL evidence"),
            Physical("inventory.recover", "Physical inventory condition orchestration and recovery evidence"),
            Physical("client.reconnect", "Paused run handoff with renewed launcher proof and stable client/world identity"),
            Physical("server.restart", "Paused run handoff with renewed proof, stable client/world, and changed server PID"),
            Unsupported("fixture.apply", "Authorized offline fixture adapter; never a production/admin command"),
            Unsupported("fault.arm", "Authorized offline fault bridge with explicit boundary allowlist"),
            Unsupported("fault.release", "Authorized offline fault bridge cleanup")
        }.ToDictionary(value => value.Operation, StringComparer.Ordinal);

    public QaCapabilityAudit Audit()
    {
        var observer = ObserverCapabilities.Load(paths.ObserverCapabilitiesPath);
        var loaded = scenarios.List().Select(summary => QaScenarioLoader.Load(
            Path.GetFullPath(Path.Combine(paths.ProjectRoot, summary.Path)))).ToArray();
        var steps = loaded.SelectMany(scenario => scenario.Steps.Select(step =>
            (scenario.Id, step.Operation, Step: step, Supported: StepSupported(step, observer)))).ToArray();
        var operations = steps.GroupBy(value => value.Operation, StringComparer.Ordinal)
            .Select(group =>
            {
                var capability = Capabilities.TryGetValue(group.Key, out var known) ? known :
                    Unsupported(group.Key, "No runtime adapter is registered");
                return new QaOperationCoverage(group.Key, capability.Level, group.Count(),
                    group.Count(value => value.Supported &&
                        capability.Level is QaAutomationLevel.Physical or QaAutomationLevel.AuthenticatedReadOnly),
                    group.Select(value => value.Id).Distinct(StringComparer.Ordinal).Count(), capability.Requirement);
            }).OrderBy(value => value.Operation, StringComparer.Ordinal).ToArray();
        var fullyBacked = loaded.Count(scenario => scenario.Steps.All(step =>
            Capabilities.TryGetValue(step.Operation, out var capability) &&
            capability.Level is QaAutomationLevel.Physical or QaAutomationLevel.AuthenticatedReadOnly &&
            StepSupported(step, observer)));
        var missingContracts = steps.Where(value => !value.Supported &&
                Capabilities.TryGetValue(value.Operation, out var capability) &&
                capability.Level is QaAutomationLevel.AuthenticatedReadOnly or QaAutomationLevel.Physical)
            .Select(value => MissingContract(value.Step)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var unsupported = operations.Where(value => value.Level is QaAutomationLevel.Unsupported or QaAutomationLevel.CaptureOnly)
            .Select(value => value.Operation).ToArray();
        return new("hytale-qa-capability-audit-v1", paths.ProfileId, loaded.Length, operations.Length,
            operations.Count(value => value.Level is QaAutomationLevel.Physical or QaAutomationLevel.AuthenticatedReadOnly),
            unsupported.Length, fullyBacked, operations, missingContracts, unsupported);
    }

    private static QaOperationCapability Physical(string operation, string requirement) =>
        new(operation, QaAutomationLevel.Physical, requirement);
    private static QaOperationCapability ReadOnly(string operation, string requirement) =>
        new(operation, QaAutomationLevel.AuthenticatedReadOnly, requirement);
    private static QaOperationCapability Capture(string operation, string requirement) =>
        new(operation, QaAutomationLevel.CaptureOnly, requirement);
    private static QaOperationCapability Unsupported(string operation, string requirement) =>
        new(operation, QaAutomationLevel.Unsupported, requirement);

    private static bool StepSupported(QaScenarioStep step, ObserverCapabilities observer)
    {
        if (!Capabilities.TryGetValue(step.Operation, out var capability) ||
            capability.Level is QaAutomationLevel.Unsupported or QaAutomationLevel.CaptureOnly) return false;
        if (step.Operation == "inventory.inspect")
        {
            var id = TargetId(step);
            return id is not null && observer.InventorySelectors.Any(selector => Glob(selector, id));
        }
        if (step.Operation == "assert.ui")
            return step.Expect is { } ui && ui.EnumerateObject()
                .All(property => observer.SemanticUiAssertions.Contains(property.Name));
        if (step.Operation == "ui.activate")
            return observer.SemanticUiOperations.Contains("ui.activate") && TargetId(step) is { } target &&
                   observer.SemanticUiTargets.Any(value => Glob(value, target));
        if (step.Operation.StartsWith("inventory.", StringComparison.Ordinal) &&
            step.Operation != "inventory.inspect")
            return observer.SemanticUiOperations.Contains(step.Operation) &&
                   TargetId(step) is { } selector && observer.InventorySelectors.Any(value => Glob(value, selector));
        if (step.Operation == "assert.count")
            return ExpectString(step, "event") is { } eventName && observer.EventTypes.Contains(eventName);
        if (step.Operation == "assert.correlated")
            return ExpectStrings(step, "events").All(observer.EventTypes.Contains);
        if (step.Operation == "assert.hash_chain")
        {
            var subject = ExpectString(step, "subject");
            if (subject is null || !observer.HashSubjects.TryGetValue(subject, out var fields)) return false;
            return step.Expect is { } expect && expect.EnumerateObject()
                .Where(property => property.Name != "subject").All(property => fields.Contains(property.Name));
        }
        if (step.Operation == "assert.visual")
            return step.Expect is { } visual && visual.EnumerateObject()
                .All(property => observer.VisualAssertions.Contains(property.Name));
        return true;
    }

    private static string MissingContract(QaScenarioStep step) => step.Operation switch
    {
        "inventory.inspect" => $"inventory selector:{TargetId(step) ?? "<missing>"}",
        "assert.count" => $"event type:{ExpectString(step, "event") ?? "<missing>"}",
        "assert.correlated" => $"event types:{string.Join(",", ExpectStrings(step, "events"))}",
        "assert.hash_chain" => $"hash proof:{ExpectString(step, "subject") ?? "<missing>"}",
        "assert.visual" => $"visual assertions:{string.Join(",", step.Expect?.EnumerateObject().Select(p => p.Name) ?? [])}",
        "assert.ui" => $"semantic UI assertions:{string.Join(",", step.Expect?.EnumerateObject().Select(p => p.Name) ?? [])}",
        "ui.activate" => $"semantic UI operation:ui.activate target:{TargetId(step) ?? "<missing>"}",
        var operation when operation.StartsWith("inventory.", StringComparison.Ordinal) =>
            $"semantic UI operation:{operation} selector:{TargetId(step) ?? "<missing>"}",
        _ => Capabilities.TryGetValue(step.Operation, out var capability) ? capability.Requirement : step.Operation
    };
    private static string? TargetId(QaScenarioStep step) => step.Target is { } target &&
        target.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
    private static string? ExpectString(QaScenarioStep step, string name) => step.Expect is { } expect &&
        expect.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static IReadOnlyList<string> ExpectStrings(QaScenarioStep step, string name) => step.Expect is { } expect &&
        expect.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!).ToArray() : [];
    private static bool Glob(string pattern, string value) => pattern.EndsWith('*')
        ? value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
        : string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

    private sealed record ObserverCapabilities(HashSet<string> InventorySelectors, HashSet<string> EventTypes,
        Dictionary<string, HashSet<string>> HashSubjects, HashSet<string> VisualAssertions,
        HashSet<string> SemanticUiOperations, HashSet<string> SemanticUiAssertions,
        HashSet<string> SemanticUiTargets)
    {
        public static ObserverCapabilities Load(string path)
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != "hytale-qa-observer-capabilities-v1")
                throw new InvalidDataException("Unsupported observer capability manifest schema.");
            static HashSet<string> Strings(JsonElement parent, string name) =>
                (parent.TryGetProperty(name, out var values) && values.ValueKind == JsonValueKind.Array
                    ? values.EnumerateArray() : []).Select(value => value.GetString() ?? "")
                    .Where(value => value.Length > 0).ToHashSet(StringComparer.Ordinal);
            var hashSubjects = root.GetProperty("hashSubjects").EnumerateObject().ToDictionary(
                property => property.Name, property => property.Value.EnumerateArray()
                    .Select(value => value.GetString() ?? "").Where(value => value.Length > 0)
                    .ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
            return new(Strings(root, "inventorySelectors"), Strings(root, "eventTypes"), hashSubjects,
                Strings(root, "visualAssertions"), Strings(root, "semanticUiOperations"),
                Strings(root, "semanticUiAssertions"), Strings(root, "semanticUiTargets"));
        }
    }
}
