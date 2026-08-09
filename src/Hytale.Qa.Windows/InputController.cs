using System.ComponentModel;
using System.Runtime.InteropServices;
using Hytale.Qa.Contracts;

namespace Hytale.Qa.Windows;

public interface INativeInputBackend
{
    void Keyboard(ushort virtualKey, KeyTransition transition);
    void MouseMoveRelative(int deltaX, int deltaY);
    void MouseMoveClient(long windowHandle, double normalizedX, double normalizedY);
    void MouseButton(MouseButton button, KeyTransition transition);
}

public sealed class SendInputBackend : INativeInputBackend
{
    public void Keyboard(ushort virtualKey, KeyTransition transition)
    {
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Union = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = transition == KeyTransition.Up ? NativeMethods.KeyEventKeyUp : 0
                }
            }
        };
        Send(input);
    }

    public void MouseMoveRelative(int deltaX, int deltaY)
    {
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput { X = deltaX, Y = deltaY, Flags = NativeMethods.MouseMove }
            }
        };
        Send(input);
    }

    public void MouseMoveClient(long windowHandle, double normalizedX, double normalizedY)
    {
        if (windowHandle == 0 || !double.IsFinite(normalizedX) || !double.IsFinite(normalizedY) ||
            normalizedX is < 0 or > 1 || normalizedY is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(normalizedX), "Client pointer coordinates must be normalized to 0..1.");
        var window = (nint)windowHandle;
        if (!NativeMethods.IsWindow(window) || !NativeMethods.GetClientRect(window, out var rect))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Pinned client window is unavailable.");
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 2 || height < 2) throw new InvalidOperationException("Pinned client area is empty.");
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not resolve the pinned client area.");
        var screenX = origin.X + (int)Math.Round(normalizedX * (width - 1));
        var screenY = origin.Y + (int)Math.Round(normalizedY * (height - 1));
        var virtualLeft = NativeMethods.GetSystemMetrics(76);
        var virtualTop = NativeMethods.GetSystemMetrics(77);
        var virtualWidth = NativeMethods.GetSystemMetrics(78);
        var virtualHeight = NativeMethods.GetSystemMetrics(79);
        if (virtualWidth < 2 || virtualHeight < 2 || screenX < virtualLeft || screenY < virtualTop ||
            screenX >= virtualLeft + virtualWidth || screenY >= virtualTop + virtualHeight)
            throw new InvalidOperationException("Resolved pointer escaped the virtual desktop.");
        var absoluteX = (int)Math.Round((screenX - virtualLeft) * 65535d / (virtualWidth - 1));
        var absoluteY = (int)Math.Round((screenY - virtualTop) * 65535d / (virtualHeight - 1));
        Send(new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput
                {
                    X = absoluteX,
                    Y = absoluteY,
                    Flags = NativeMethods.MouseMove | NativeMethods.MouseMoveAbsolute | NativeMethods.MouseVirtualDesk
                }
            }
        });
    }

    public void MouseButton(MouseButton button, KeyTransition transition)
    {
        var (flags, data) = (button, transition) switch
        {
            (Hytale.Qa.Contracts.MouseButton.Left, KeyTransition.Down) => (NativeMethods.MouseLeftDown, 0u),
            (Hytale.Qa.Contracts.MouseButton.Left, KeyTransition.Up) => (NativeMethods.MouseLeftUp, 0u),
            (Hytale.Qa.Contracts.MouseButton.Right, KeyTransition.Down) => (NativeMethods.MouseRightDown, 0u),
            (Hytale.Qa.Contracts.MouseButton.Right, KeyTransition.Up) => (NativeMethods.MouseRightUp, 0u),
            (Hytale.Qa.Contracts.MouseButton.Middle, KeyTransition.Down) => (NativeMethods.MouseMiddleDown, 0u),
            (Hytale.Qa.Contracts.MouseButton.Middle, KeyTransition.Up) => (NativeMethods.MouseMiddleUp, 0u),
            (Hytale.Qa.Contracts.MouseButton.X1, KeyTransition.Down) => (NativeMethods.MouseXDown, 1u),
            (Hytale.Qa.Contracts.MouseButton.X1, KeyTransition.Up) => (NativeMethods.MouseXUp, 1u),
            (Hytale.Qa.Contracts.MouseButton.X2, KeyTransition.Down) => (NativeMethods.MouseXDown, 2u),
            (Hytale.Qa.Contracts.MouseButton.X2, KeyTransition.Up) => (NativeMethods.MouseXUp, 2u),
            _ => throw new ArgumentOutOfRangeException(nameof(button))
        };
        var input = new NativeMethods.Input
        {
            Type = NativeMethods.InputMouse,
            Union = new NativeMethods.InputUnion
            {
                Mouse = new NativeMethods.MouseInput { Flags = flags, MouseData = data }
            }
        };
        Send(input);
    }

    private static void Send(NativeMethods.Input input)
    {
        var sent = NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeMethods.Input>());
        if (sent != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput did not inject the requested event. UIPI may have blocked it.");
    }
}

public sealed class FailClosedInputController : IDisposable
{
    private readonly ClientLeaseManager leases;
    private readonly INativeInputBackend backend;
    private readonly Func<SafetyCheck> armingGuard;
    private readonly HashSet<ushort> heldKeys = [];
    private readonly HashSet<MouseButton> heldButtons = [];
    private readonly object sync = new();
    private readonly Timer watchdog;
    private string? leaseId;
    private SafetyCheck watchdogState = SafetyCheck.Fail("session.not_bound", "Input controller is not bound to a lease.");
    private bool disposed;

    public FailClosedInputController(ClientLeaseManager leases, INativeInputBackend backend, Func<SafetyCheck> armingGuard)
    {
        this.leases = leases;
        this.backend = backend;
        this.armingGuard = armingGuard;
        watchdog = new Timer(PollSafety, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public SafetyCheck WatchdogState { get { lock (sync) return watchdogState; } }
    public int HeldInputCount { get { lock (sync) return heldKeys.Count + heldButtons.Count; } }

    public void Bind(string activeLeaseId)
    {
        var check = leases.Revalidate(activeLeaseId, false);
        if (!check.Safe) throw new InvalidOperationException($"{check.Code}: {check.Message}");
        lock (sync)
        {
            leaseId = activeLeaseId;
            watchdogState = SafetyCheck.Pass("watchdog armed");
            watchdog.Change(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }
    }

    public void Key(ushort virtualKey, KeyTransition transition)
    {
        lock (sync)
        {
            EnsureSafe();
            backend.Keyboard(virtualKey, transition);
            if (transition == KeyTransition.Down) heldKeys.Add(virtualKey); else heldKeys.Remove(virtualKey);
        }
    }

    public void Mouse(MouseButton button, KeyTransition transition)
    {
        lock (sync)
        {
            EnsureSafe();
            backend.MouseButton(button, transition);
            if (transition == KeyTransition.Down) heldButtons.Add(button); else heldButtons.Remove(button);
        }
    }

    public void MoveRelative(int deltaX, int deltaY)
    {
        lock (sync)
        {
            EnsureSafe();
            if (Math.Abs((long)deltaX) > 32767 || Math.Abs((long)deltaY) > 32767)
                throw new ArgumentOutOfRangeException(nameof(deltaX), "Relative movement is limited to 32767 units per axis per event.");
            backend.MouseMoveRelative(deltaX, deltaY);
        }
    }

    public void MoveClient(long windowHandle, double normalizedX, double normalizedY)
    {
        lock (sync)
        {
            EnsureSafe();
            if (leases.Current?.Identity.WindowHandle != windowHandle)
                throw new InvalidOperationException("Pointer target does not match the pinned client HWND.");
            backend.MouseMoveClient(windowHandle, normalizedX, normalizedY);
        }
    }

    public void ReleaseAll()
    {
        lock (sync)
        {
            foreach (var key in heldKeys.ToArray()) TryRelease(() => backend.Keyboard(key, KeyTransition.Up));
            foreach (var button in heldButtons.ToArray()) TryRelease(() => backend.MouseButton(button, KeyTransition.Up));
            heldKeys.Clear();
            heldButtons.Clear();
        }
    }

    private void EnsureSafe()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!watchdogState.Safe) throw new InvalidOperationException($"{watchdogState.Code}: {watchdogState.Message}");
        var armed = armingGuard();
        if (!armed.Safe) throw new InvalidOperationException($"{armed.Code}: {armed.Message}");
        if (leaseId is null) throw new InvalidOperationException("Input controller is not bound to a lease.");
        var check = leases.Revalidate(leaseId, true);
        if (check.Safe) return;
        ReleaseAll();
        throw new InvalidOperationException($"{check.Code}: {check.Message}");
    }

    private static void TryRelease(Action release)
    {
        try { release(); } catch (Win32Exception) { }
    }

    private void PollSafety(object? _)
    {
        lock (sync)
        {
            if (disposed || leaseId is null || !watchdogState.Safe) return;
            var armed = armingGuard();
            var check = armed.Safe ? leases.Revalidate(leaseId, true) : armed;
            if (check.Safe) return;
            watchdogState = check;
            ReleaseAll();
            watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        ReleaseAll();
        disposed = true;
        watchdog.Dispose();
        GC.SuppressFinalize(this);
    }
}
