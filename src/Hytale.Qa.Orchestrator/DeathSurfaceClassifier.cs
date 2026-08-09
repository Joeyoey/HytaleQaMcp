using System.Buffers.Binary;

namespace Hytale.Qa.Orchestrator;

internal enum DeathSurfaceState { Ready, Unknown }

internal sealed record DeathSurfaceClassification(
    DeathSurfaceState State,
    string Code,
    string Message,
    double RespawnButtonBlueRatio,
    double DeathAccentOrangeRatio,
    double CenterOverlayDarkRatio);

/// <summary>
/// Conservative classifier for Hytale's native death overlay. A respawn click
/// is permitted only when this visual proof is paired with the authenticated
/// observer's authoritative dead=true state.
/// </summary>
internal static class DeathSurfaceClassifier
{
    public static DeathSurfaceClassification Classify(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            return Unknown("death-surface-bitmap-invalid", "Capture is not a supported BMP image.");
        var offset = Int32(bytes, 10);
        var width = Int32(bytes, 18);
        var signedHeight = Int32(bytes, 22);
        var bitsPerPixel = UInt16(bytes, 28);
        var compression = Int32(bytes, 30);
        var height = Math.Abs(signedHeight);
        if (width < 800 || height < 600 || bitsPerPixel != 32 || compression != 0 || offset < 54)
            return Unknown("death-surface-bitmap-unsupported",
                "Capture must be an uncompressed BGRA32 Hytale window of at least 800x600.");
        var required = checked((long)offset + (long)width * height * 4);
        if (required > bytes.Length)
            return Unknown("death-surface-bitmap-truncated", "Capture pixel data is truncated.");

        var respawnBlue = Ratio(bytes, offset, width, signedHeight, 0.42, 0.58, 0.49, 0.56,
            static (red, green, blue) => blue >= 90 && blue >= red * 1.15 && blue >= green * 1.05);
        var deathOrange = Ratio(bytes, offset, width, signedHeight, 0.40, 0.60, 0.30, 0.49,
            static (red, green, blue) => red >= 150 && green is >= 50 and <= 170 &&
                                         red >= green * 1.4 && green >= blue * 1.4);
        var overlayDark = Ratio(bytes, offset, width, signedHeight, 0.34, 0.66, 0.35, 0.69,
            static (red, green, blue) => Math.Max(red, Math.Max(green, blue)) <= 120);
        if (respawnBlue >= 0.25 && deathOrange >= 0.02 && overlayDark >= 0.60)
            return new(DeathSurfaceState.Ready, "native-death-surface-observed",
                "Native death overlay, accent, and respawn control were observed in stable client regions.",
                respawnBlue, deathOrange, overlayDark);
        return new(DeathSurfaceState.Unknown, "native-death-surface-unproven",
            "The capture did not prove Hytale's native death overlay and respawn control.",
            respawnBlue, deathOrange, overlayDark);
    }

    private static DeathSurfaceClassification Unknown(string code, string message) =>
        new(DeathSurfaceState.Unknown, code, message, 0, 0, 0);

    private static double Ratio(byte[] bytes, int offset, int width, int signedHeight,
        double left, double right, double top, double bottom,
        Func<byte, byte, byte, bool> predicate)
    {
        var height = Math.Abs(signedHeight);
        var x0 = Math.Clamp((int)(width * left), 0, width - 1);
        var x1 = Math.Clamp((int)(width * right), x0 + 1, width);
        var y0 = Math.Clamp((int)(height * top), 0, height - 1);
        var y1 = Math.Clamp((int)(height * bottom), y0 + 1, height);
        var matched = 0;
        var count = 0;
        for (var y = y0; y < y1; y++)
        {
            var sourceY = signedHeight > 0 ? height - 1 - y : y;
            for (var x = x0; x < x1; x++)
            {
                var pixel = checked(offset + (sourceY * width + x) * 4);
                if (predicate(bytes[pixel + 2], bytes[pixel + 1], bytes[pixel])) matched++;
                count++;
            }
        }
        return count == 0 ? 0 : (double)matched / count;
    }

    private static int Int32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static ushort UInt16(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
}
