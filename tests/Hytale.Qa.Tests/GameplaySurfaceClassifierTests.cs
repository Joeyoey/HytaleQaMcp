using System.Buffers.Binary;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class GameplaySurfaceClassifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-gameplay-surface", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AcceptsNativeHudPatternAndAbstainsWhenStatusBarsAreDimmed()
    {
        Directory.CreateDirectory(root);
        var ready = Write("ready.bmp", dimmed: false);
        var dimmed = Write("dimmed.bmp", dimmed: true);

        var accepted = GameplaySurfaceClassifier.Classify(ready);
        var refused = GameplaySurfaceClassifier.Classify(dimmed);

        Assert.Equal(GameplaySurfaceState.Ready, accepted.State);
        Assert.True(accepted.HealthBarChromaRatio >= 0.18);
        Assert.True(accepted.StaminaBarChromaRatio >= 0.18);
        Assert.True(accepted.HotbarBlueRatio >= 0.40);
        Assert.Equal(GameplaySurfaceState.Unknown, refused.State);
        Assert.Equal("gameplay-hud-unproven", refused.Code);
    }

    private string Write(string name, bool dimmed)
    {
        const int width = 1000;
        const int height = 700;
        const int offset = 54;
        var bytes = new byte[offset + width * height * 4];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        Put32(bytes, 2, bytes.Length); Put32(bytes, 10, offset); Put32(bytes, 14, 40);
        Put32(bytes, 18, width); Put32(bytes, 22, height);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(28, 2), 32);
        Paint(bytes, width, height, 0.32, 0.50, 0.87, 0.91,
            dimmed ? ((byte)60, (byte)35, (byte)40) : ((byte)230, (byte)55, (byte)95));
        Paint(bytes, width, height, 0.50, 0.68, 0.87, 0.91,
            dimmed ? ((byte)65, (byte)45, (byte)25) : ((byte)235, (byte)145, (byte)45));
        Paint(bytes, width, height, 0.26, 0.69, 0.89, 0.98, (30, 50, 85));
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void Paint(byte[] bytes, int width, int height,
        double left, double right, double top, double bottom, (byte R, byte G, byte B) color)
    {
        for (var y = (int)(height * top); y < (int)(height * bottom); y++)
            for (var x = (int)(width * left); x < (int)(width * right); x++)
            {
                var sourceY = height - 1 - y;
                var pixel = 54 + (sourceY * width + x) * 4;
                bytes[pixel] = color.B; bytes[pixel + 1] = color.G; bytes[pixel + 2] = color.R; bytes[pixel + 3] = 255;
            }
    }

    private static void Put32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
