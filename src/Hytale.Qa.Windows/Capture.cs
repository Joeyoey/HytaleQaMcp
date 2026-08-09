using System.ComponentModel;
using System.Runtime.InteropServices;
using Hytale.Qa.Contracts;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Hytale.Qa.Windows;

public interface IWindowCaptureBackend
{
    CaptureCapability Capability { get; }
    CaptureArtifact Capture(ProcessIdentity identity, string destinationPath);
}

public sealed class VisibleWindowGdiCaptureBackend : IWindowCaptureBackend
{
    public CaptureCapability Capability { get; } = new(
        "win32-gdi-visible-client-area",
        CaptureAvailability.Available,
        WorksWhenOccluded: false,
        WorksWhenMinimized: false,
        Formats: ["image/bmp"],
        Limitation: "Captures pixels currently visible on the desktop. It is not Windows.Graphics.Capture and cannot prove occluded or minimized rendering.");

    public CaptureArtifact Capture(ProcessIdentity identity, string destinationPath)
    {
        var window = (nint)identity.WindowHandle;
        if (!NativeMethods.IsWindow(window)) throw new InvalidOperationException("Window is no longer valid.");
        if (NativeMethods.IsIconic(window)) throw new InvalidOperationException("Visible GDI capture does not support minimized windows.");
        if (!NativeMethods.GetClientRect(window, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("Window client area is empty.");
        var screenDc = nint.Zero;
        var memoryDc = nint.Zero;
        var bitmap = nint.Zero;
        var previous = nint.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(nint.Zero);
            if (screenDc == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous = NativeMethods.SelectObject(memoryDc, bitmap);
            if (previous == nint.Zero || previous == new nint(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!NativeMethods.BitBlt(memoryDc, 0, 0, width, height, screenDc, origin.X, origin.Y, NativeMethods.Srccopy))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var info = new NativeMethods.BitmapInfo
            {
                Header = new NativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BitmapInfoHeader>(),
                    Width = width,
                    Height = height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                    SizeImage = (uint)pixels.Length
                }
            };
            if (NativeMethods.GetDIBits(memoryDc, bitmap, 0, (uint)height, pixels, ref info, NativeMethods.DibRgbColors) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var fullPath = Path.GetFullPath(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            if (File.Exists(fullPath)) throw new IOException("Screenshot destination already exists; evidence is append-only.");
            WriteBitmap(fullPath, width, height, pixels);
            return new(fullPath, "image/bmp", width, height, DateTimeOffset.UtcNow,
                EvidenceFile.ComputeSha256(fullPath), Capability, GetDisplayMode(window));
        }
        finally
        {
            if (memoryDc != nint.Zero && previous != nint.Zero && previous != new nint(-1)) NativeMethods.SelectObject(memoryDc, previous);
            if (bitmap != nint.Zero) NativeMethods.DeleteObject(bitmap);
            if (memoryDc != nint.Zero) NativeMethods.DeleteDC(memoryDc);
            if (screenDc != nint.Zero) NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static string GetDisplayMode(nint window)
    {
        var windowRect = ClientAreaCrop.GetCaptureFrameRect(window);
        if (!NativeMethods.GetClientRect(window, out var clientRect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return ClientAreaCrop.Resolve(
            windowRect, clientRect, origin,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top).DisplayMode;
    }

    private static void WriteBitmap(string path, int width, int height, byte[] pixels)
    {
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4D42);
        writer.Write(fileHeaderSize + infoHeaderSize + pixels.Length);
        writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(fileHeaderSize + infoHeaderSize);
        writer.Write(infoHeaderSize); writer.Write(width); writer.Write(height);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(pixels.Length);
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
        writer.Write(pixels);
    }
}

public sealed class WindowsGraphicsCaptureBackend : IWindowCaptureBackend
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

    public CaptureCapability Capability => new(
        "windows-graphics-capture-window",
        IsSupported() ? CaptureAvailability.Available : CaptureAvailability.Unsupported,
        WorksWhenOccluded: true,
        WorksWhenMinimized: false,
        Formats: ["image/bmp"],
        Limitation: IsSupported()
            ? "Captures and crops the leased HWND to its client area when visible or occluded. Minimized windows are rejected because Windows may stop producing current frames. SDR BGRA8 output may tone-map HDR content."
            : "Windows.Graphics.Capture requires Windows 10 build 18362 or later and GraphicsCaptureSession.IsSupported().");

    public CaptureArtifact Capture(ProcessIdentity identity, string destinationPath)
    {
        if (!IsSupported()) throw new NotSupportedException(Capability.Limitation);
        ValidateWindow(identity, rejectMinimized: true);

        var finalPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new InvalidOperationException("Capture destination has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(finalPath)) throw new IOException("Screenshot destination already exists; evidence is append-only.");
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(finalPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var dimensions = Task.Run(
                () => CaptureAsync(identity, temporaryPath, FrameTimeout),
                CancellationToken.None).GetAwaiter().GetResult();
            ValidateWindow(identity, rejectMinimized: true);
            File.Move(temporaryPath, finalPath, overwrite: false);
            return new(finalPath, "image/bmp", dimensions.Width, dimensions.Height,
                DateTimeOffset.UtcNow, EvidenceFile.ComputeSha256(finalPath), Capability,
                dimensions.DisplayMode);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static bool IsSupported() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) && GraphicsCaptureSession.IsSupported();

    private static async Task<(int Width, int Height, string DisplayMode)> CaptureAsync(
        ProcessIdentity identity,
        string temporaryPath,
        TimeSpan timeout)
    {
        var item = WindowsGraphicsCaptureInterop.CreateForWindow((nint)identity.WindowHandle);
        using var nativeDevice = CreateNativeDevice();
        using var direct3DDevice = WindowsGraphicsCaptureInterop.CreateDirect3DDevice(nativeDevice);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            item.Size);
        using var captureSession = framePool.CreateCaptureSession(item);

        var frameReady = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            if (!frameReady.TrySetResult(frame)) frame.Dispose();
        }

        framePool.FrameArrived += OnFrameArrived;
        try
        {
            captureSession.StartCapture();
            using var frame = await frameReady.Task.WaitAsync(timeout).ConfigureAwait(false);
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0)
                throw new InvalidOperationException("Windows.Graphics.Capture returned an empty frame.");

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface,
                BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
            using var memoryStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, memoryStream)
                .AsTask().ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            var crop = ClientAreaCrop.ForWindow((nint)identity.WindowHandle, size.Width, size.Height);
            encoder.BitmapTransform.Bounds = new BitmapBounds
            {
                X = checked((uint)crop.X),
                Y = checked((uint)crop.Y),
                Width = checked((uint)crop.Width),
                Height = checked((uint)crop.Height)
            };
            await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            if (memoryStream.Size == 0 || memoryStream.Size > int.MaxValue)
                throw new InvalidOperationException("Encoded capture size is invalid.");

            memoryStream.Seek(0);
            using var reader = new DataReader(memoryStream.GetInputStreamAt(0));
            var byteCount = checked((uint)memoryStream.Size);
            var loaded = await reader.LoadAsync(byteCount).AsTask().ConfigureAwait(false);
            if (loaded != byteCount) throw new EndOfStreamException("Encoded capture was truncated.");
            var bytes = new byte[byteCount];
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(temporaryPath, bytes).ConfigureAwait(false);
            return (crop.Width, crop.Height, crop.DisplayMode);
        }
        finally
        {
            framePool.FrameArrived -= OnFrameArrived;
        }
    }

    private static ID3D11Device CreateNativeDevice()
    {
        var featureLevels = new[]
        {
            Vortice.Direct3D.FeatureLevel.Level_11_1,
            Vortice.Direct3D.FeatureLevel.Level_11_0,
            Vortice.Direct3D.FeatureLevel.Level_10_1,
            Vortice.Direct3D.FeatureLevel.Level_10_0
        };
        var result = D3D11.D3D11CreateDevice(
            null,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device? device,
            out Vortice.Direct3D.FeatureLevel _);
        if (result.Failure || device is null)
            throw new COMException("D3D11CreateDevice failed for Windows.Graphics.Capture.", result.Code);
        return device!;
    }

    private static void ValidateWindow(ProcessIdentity identity, bool rejectMinimized)
    {
        var window = (nint)identity.WindowHandle;
        if (!NativeMethods.IsWindow(window)) throw new InvalidOperationException("Leased window is no longer valid.");
        _ = NativeMethods.GetWindowThreadProcessId(window, out var ownerPid);
        if (ownerPid != identity.ProcessId)
            throw new InvalidOperationException("Leased window ownership changed during capture.");
        if (rejectMinimized && NativeMethods.IsIconic(window))
            throw new InvalidOperationException("Windows.Graphics.Capture rejects minimized windows to avoid stale-frame evidence.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* Preserve the original capture failure. */ }
    }
}

internal sealed record ClientAreaCrop(int X, int Y, int Width, int Height, string DisplayMode)
{
    internal static ClientAreaCrop ForWindow(nint window, int frameWidth, int frameHeight)
    {
        var windowRect = GetCaptureFrameRect(window);
        if (!NativeMethods.GetClientRect(window, out var clientRect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var origin = new NativeMethods.Point();
        if (!NativeMethods.ClientToScreen(window, ref origin))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return Resolve(windowRect, clientRect, origin, frameWidth, frameHeight);
    }

    internal static NativeMethods.Rect GetCaptureFrameRect(nint window)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                window,
                NativeMethods.DwmExtendedFrameBounds,
                out var frameRect,
                checked((uint)Marshal.SizeOf<NativeMethods.Rect>())) == 0)
            return frameRect;
        if (!NativeMethods.GetWindowRect(window, out frameRect))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return frameRect;
    }

    internal static ClientAreaCrop Resolve(
        NativeMethods.Rect windowRect,
        NativeMethods.Rect clientRect,
        NativeMethods.Point clientOrigin,
        int frameWidth,
        int frameHeight)
    {
        var width = clientRect.Right - clientRect.Left;
        var height = clientRect.Bottom - clientRect.Top;
        var x = clientOrigin.X - windowRect.Left;
        var y = clientOrigin.Y - windowRect.Top;
        if (width <= 0 || height <= 0 || x < 0 || y < 0 ||
            x + width > frameWidth || y + height > frameHeight)
            throw new InvalidOperationException("Window client area is outside the captured frame.");
        var displayMode = x == 0 && y == 0 && width == frameWidth && height == frameHeight
            ? "borderless"
            : "windowed";
        return new(x, y, width, height, displayMode);
    }
}

internal static class EvidenceFile
{
    internal static string ComputeSha256(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(input));
    }
}

internal static class WindowsGraphicsCaptureInterop
{
    private const string GraphicsCaptureItemRuntimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
    private static readonly Guid GraphicsCaptureItemInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    internal static GraphicsCaptureItem CreateForWindow(nint window)
    {
        var className = nint.Zero;
        var factory = nint.Zero;
        var item = nint.Zero;
        IGraphicsCaptureItemInterop? interop = null;
        try
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(
                GraphicsCaptureItemRuntimeClass,
                GraphicsCaptureItemRuntimeClass.Length,
                out className));
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(
                className,
                GraphicsCaptureItemInteropGuid,
                out factory));
            interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factory);
            Marshal.ThrowExceptionForHR(interop.CreateForWindow(window, GraphicsCaptureItemGuid, out item));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != nint.Zero) Marshal.Release(item);
            if (interop is not null && Marshal.IsComObject(interop)) Marshal.FinalReleaseComObject(interop);
            if (factory != nint.Zero) Marshal.Release(factory);
            if (className != nint.Zero) WindowsDeleteString(className);
        }
    }

    internal static IDirect3DDevice CreateDirect3DDevice(ID3D11Device nativeDevice)
    {
        using var dxgiDevice = nativeDevice.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(
            dxgiDevice.NativePointer,
            out var inspectable));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { if (inspectable != nint.Zero) Marshal.Release(inspectable); }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint window, in Guid iid, out nint result);

        [PreserveSig]
        int CreateForMonitor(nint monitor, in Guid iid, out nint result);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        int length,
        out nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint classId, in Guid iid, out nint factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}

public sealed class UnsupportedWindowsGraphicsCaptureBackend : IWindowCaptureBackend
{
    public CaptureCapability Capability { get; } = new(
        "windows-graphics-capture",
        CaptureAvailability.Unsupported,
        WorksWhenOccluded: true,
        WorksWhenMinimized: false,
        Formats: [],
        Limitation: "Windows.Graphics.Capture was explicitly disabled by configuration.");

    public CaptureArtifact Capture(ProcessIdentity identity, string destinationPath) =>
        throw new NotSupportedException(Capability.Limitation);
}
