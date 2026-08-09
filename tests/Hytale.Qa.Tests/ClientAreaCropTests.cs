using Hytale.Qa.Windows;

namespace Hytale.Qa.Tests;

public sealed class ClientAreaCropTests
{
    [Fact]
    public void CropsDecoratedWindowToConfiguredClientViewport()
    {
        var crop = ClientAreaCrop.Resolve(
            new NativeMethods.Rect { Left = 100, Top = 50, Right = 1702, Bottom = 982 },
            new NativeMethods.Rect { Left = 0, Top = 0, Right = 1600, Bottom = 900 },
            new NativeMethods.Point { X = 101, Y = 81 },
            1602,
            932);

        Assert.Equal(new ClientAreaCrop(1, 31, 1600, 900, "windowed"), crop);
    }

    [Fact]
    public void PreservesBorderlessClientFrame()
    {
        var crop = ClientAreaCrop.Resolve(
            new NativeMethods.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 },
            new NativeMethods.Rect { Left = 0, Top = 0, Right = 2560, Bottom = 1440 },
            new NativeMethods.Point { X = 0, Y = 0 },
            2560,
            1440);

        Assert.Equal(new ClientAreaCrop(0, 0, 2560, 1440, "borderless"), crop);
    }

    [Fact]
    public void RejectsClientAreaOutsideCapturedFrame()
    {
        Assert.Throws<InvalidOperationException>(() => ClientAreaCrop.Resolve(
            new NativeMethods.Rect { Left = 0, Top = 0, Right = 100, Bottom = 100 },
            new NativeMethods.Rect { Left = 0, Top = 0, Right = 100, Bottom = 100 },
            new NativeMethods.Point { X = 1, Y = 1 },
            100,
            100));
    }
}
