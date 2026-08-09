using Hytale.Qa.Contracts;
using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class InputControllerTests
{
    [Fact]
    public void ReleasesHeldInputsOnDispose()
    {
        var identity = Identity();
        var inspector = new FakeInspector(identity) { Foreground = true };
        var leases = new ClientLeaseManager(inspector);
        var lease = leases.Acquire(identity.ProcessId, Policy(identity));
        var backend = new RecordingInputBackend();
        using (var controller = new FailClosedInputController(leases, backend, () => SafetyCheck.Pass()))
        {
            controller.Bind(lease.LeaseId);
            controller.Key(0x57, KeyTransition.Down);
            controller.Mouse(MouseButton.Left, KeyTransition.Down);
            Assert.Equal(2, controller.HeldInputCount);
            controller.ReleaseAll();
            Assert.Equal(0, controller.HeldInputCount);
        }
        Assert.Contains("key:87:Up", backend.Events);
        Assert.Contains("mouse:Left:Up", backend.Events);
    }

    [Fact]
    public void RefusesInputWhenFocusIsLostAndReleasesHeldKey()
    {
        var identity = Identity();
        var inspector = new FakeInspector(identity) { Foreground = true };
        var leases = new ClientLeaseManager(inspector);
        var lease = leases.Acquire(identity.ProcessId, Policy(identity));
        var backend = new RecordingInputBackend();
        using var controller = new FailClosedInputController(leases, backend, () => SafetyCheck.Pass());
        controller.Bind(lease.LeaseId);
        controller.Key(0x57, KeyTransition.Down);
        inspector.Foreground = false;
        var exception = Assert.Throws<InvalidOperationException>(() => controller.MoveRelative(1, 1));
        Assert.Contains("window.not_foreground", exception.Message);
        Assert.Contains("key:87:Up", backend.Events);
    }

    [Fact]
    public void WatchdogReleasesHeldInputWithoutAnotherRpc()
    {
        var identity = Identity();
        var inspector = new FakeInspector(identity) { Foreground = true };
        var leases = new ClientLeaseManager(inspector);
        var lease = leases.Acquire(identity.ProcessId, Policy(identity));
        var backend = new RecordingInputBackend();
        using var controller = new FailClosedInputController(leases, backend, () => SafetyCheck.Pass());
        controller.Bind(lease.LeaseId);
        controller.Key(0x57, KeyTransition.Down);
        inspector.Foreground = false;
        Assert.True(SpinWait.SpinUntil(() => backend.Events.Contains("key:87:Up"), TimeSpan.FromSeconds(2)));
        Assert.False(controller.WatchdogState.Safe);
    }

    private static ProcessIdentity Identity() => new(42, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), @"C:\qa\HytaleClient.exe", new string('A', 64), 123);
    private static SessionSafetyPolicy Policy(ProcessIdentity identity) => new(
        "session", "nonce", AssistanceMode.GuidedPhysical, true, "127.0.0.1:5542", "HytaleClient.exe",
        [identity.ExecutablePath], [identity.Sha256]);

    private sealed class FakeInspector(ProcessIdentity identity) : IProcessInspector
    {
        public bool Foreground { get; set; }
        public ProcessIdentity Inspect(int processId) => identity;
        public int CountByExecutableName(string executableName) => 1;
        public SafetyCheck ValidateWindowIdentity(ProcessIdentity candidate) => SafetyCheck.Pass();
        public bool IsForeground(ProcessIdentity candidate) => Foreground;
        public bool TrySetForeground(ProcessIdentity candidate) { Foreground = true; return true; }
    }

    private sealed class RecordingInputBackend : INativeInputBackend
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Events { get; } = new();
        public void Keyboard(ushort virtualKey, KeyTransition transition) => Events.Enqueue($"key:{virtualKey}:{transition}");
        public void MouseMoveRelative(int deltaX, int deltaY) => Events.Enqueue($"move:{deltaX}:{deltaY}");
        public void MouseMoveClient(long windowHandle, double normalizedX, double normalizedY) =>
            Events.Enqueue($"pointer:{windowHandle}:{normalizedX:F3}:{normalizedY:F3}");
        public void MouseButton(MouseButton button, KeyTransition transition) => Events.Enqueue($"mouse:{button}:{transition}");
    }
}
