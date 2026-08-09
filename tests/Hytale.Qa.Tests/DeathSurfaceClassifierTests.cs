using System.Buffers.Binary;
using Hytale.Qa.Orchestrator;

namespace Hytale.Qa.Tests;

public sealed class DeathSurfaceClassifierTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "hytale-qa-death-surface", Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequiresRespawnControlDeathAccentAndDarkOverlayTogether()
    {
        Directory.CreateDirectory(root);
        var proven = Write("proven.bmp", includeRespawn: true, includeAccent: true, includeOverlay: true);
        var noButton = Write("no-button.bmp", includeRespawn: false, includeAccent: true, includeOverlay: true);
        var normalHud = Write("normal-hud.bmp", includeRespawn: false, includeAccent: false, includeOverlay: false);

        var accepted = DeathSurfaceClassifier.Classify(proven);
        var missingButton = DeathSurfaceClassifier.Classify(noButton);
        var gameplay = DeathSurfaceClassifier.Classify(normalHud);

        Assert.Equal(DeathSurfaceState.Ready, accepted.State);
        Assert.True(accepted.RespawnButtonBlueRatio >= 0.25);
        Assert.True(accepted.DeathAccentOrangeRatio >= 0.02);
        Assert.True(accepted.CenterOverlayDarkRatio >= 0.60);
        Assert.Equal(DeathSurfaceState.Unknown, missingButton.State);
        Assert.Equal(DeathSurfaceState.Unknown, gameplay.State);
    }

    private string Write(string name, bool includeRespawn, bool includeAccent, bool includeOverlay)
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
        Paint(bytes, width, height, 0, 1, 0, 1, (95, 115, 125));
        if (includeOverlay)
            Paint(bytes, width, height, 0.34, 0.66, 0.35, 0.69, (45, 38, 42));
        if (includeAccent)
            Paint(bytes, width, height, 0.45, 0.55, 0.35, 0.42, (220, 95, 35));
        if (includeRespawn)
            Paint(bytes, width, height, 0.44, 0.56, 0.50, 0.55, (55, 105, 175));
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
                bytes[pixel] = color.B; bytes[pixel + 1] = color.G;
                bytes[pixel + 2] = color.R; bytes[pixel + 3] = 255;
            }
    }

    private static void Put32(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
