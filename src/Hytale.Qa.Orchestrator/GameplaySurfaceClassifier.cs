using System.Buffers.Binary;

namespace Hytale.Qa.Orchestrator;

internal enum GameplaySurfaceState { Ready, Unknown }

internal sealed record GameplaySurfaceClassification(
    GameplaySurfaceState State,
    string Code,
    string Message,
    double HealthBarChromaRatio,
    double StaminaBarChromaRatio,
    double HotbarBlueRatio);

/// <summary>
/// Conservative, deterministic HUD classifier for Windows Graphics Capture
/// BGRA32 bitmaps. It proves the normal player-control surface by requiring
/// both bright status bars and the native blue hotbar in their stable,
/// resolution-independent HUD regions. Anything else is Unknown; it is never
/// guessed to be input-safe.
/// </summary>
internal static class GameplaySurfaceClassifier
{
    public static GameplaySurfaceClassification Classify(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 54 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
            return Unknown("surface-bitmap-invalid", "Capture is not a supported BMP image.");
        var offset = Int32(bytes, 10);
        var width = Int32(bytes, 18);
        var signedHeight = Int32(bytes, 22);
        var bitsPerPixel = UInt16(bytes, 28);
        var compression = Int32(bytes, 30);
        var height = Math.Abs(signedHeight);
        if (width < 800 || height < 600 || bitsPerPixel != 32 || compression != 0 || offset < 54)
            return Unknown("surface-bitmap-unsupported",
                "Capture must be an uncompressed BGRA32 gameplay window of at least 800x600.");
        var required = checked((long)offset + (long)width * height * 4);
        if (required > bytes.Length)
            return Unknown("surface-bitmap-truncated", "Capture pixel data is truncated.");

        var health = ChromaRatio(bytes, offset, width, signedHeight, 0.32, 0.50, 0.87, 0.91);
        var stamina = ChromaRatio(bytes, offset, width, signedHeight, 0.50, 0.68, 0.87, 0.91);
        var hotbar = BlueRatio(bytes, offset, width, signedHeight, 0.26, 0.69, 0.89, 0.98);
        if (health >= 0.18 && stamina >= 0.18 && hotbar >= 0.40)
            return new(GameplaySurfaceState.Ready, "gameplay-hud-observed",
                "Bright health/stamina bars and the native hotbar were observed in stable HUD regions.",
                health, stamina, hotbar);
        return new(GameplaySurfaceState.Unknown, "gameplay-hud-unproven",
            "The capture did not prove the unobstructed native gameplay HUD; physical input remains disarmed.",
            health, stamina, hotbar);
    }

    private static GameplaySurfaceClassification Unknown(string code, string message) =>
        new(GameplaySurfaceState.Unknown, code, message, 0, 0, 0);

    private static double ChromaRatio(byte[] bytes, int offset, int width, int signedHeight,
        double left, double right, double top, double bottom) => Ratio(bytes, offset, width, signedHeight,
        left, right, top, bottom, static (red, green, blue) =>
        {
            var maximum = Math.Max(red, Math.Max(green, blue));
            var minimum = Math.Min(red, Math.Min(green, blue));
            return maximum >= 150 && maximum - minimum >= 55;
        });

    private static double BlueRatio(byte[] bytes, int offset, int width, int signedHeight,
        double left, double right, double top, double bottom) => Ratio(bytes, offset, width, signedHeight,
        left, right, top, bottom, static (red, green, blue) =>
            blue >= 45 && blue >= red * 1.2 && blue >= green * 1.05);

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
