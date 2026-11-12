using MySnip.Core.Models;
using Xunit;

namespace MySnip.Core.Tests;

public sealed class CaptureCommandTests
{
    [Fact]
    public void CombinesOutputsOnNegativeDesktop()
    {
        var directory = Directory.GetCurrentDirectory();
        var capture = CaptureCommand.Parse([
            "--area", "-1280", "-500", "1600", "900", "--hold", "--delay", ".5", "--shadow",
            "-o", "clipboard;pin;no-auto-save;silent", "--output", "result.png", "-o", "pin"
        ], directory);

        Assert.Equal(CaptureTarget.Area, capture.Target);
        Assert.Equal(new PixelRect(-1280, -500, 1600, 900), capture.Region);
        Assert.Equal(0.5, capture.Delay);
        Assert.True(capture.Hold && capture.Shadow && capture.Silent && capture.SkipAutoSave);
        Assert.Equal(new CaptureOutputRequest[]
        {
            new(CaptureDestination.Clipboard), new(CaptureDestination.Pin),
            new(CaptureDestination.File, Path.Combine(directory, "result.png"))
        }, capture.Outputs);
    }

    [Fact]
    public void RejectsInvalidInput()
    {
        string[][] invalid = [
            ["--last", "--full"],
            ["--area", "2147483640", "0", "20", "10"],
            ["--area", "0", "0", "-20", "10"],
            ["--output", "text.txt"],
            ["--delay", "NaN"]
        ];
        foreach (var args in invalid)
            Assert.Throws<ArgumentException>(() => CaptureCommand.Parse(args, Directory.GetCurrentDirectory()));
    }

    [Fact]
    public void AcceptsOptionalDelayInCustomArea()
    {
        Assert.True(CaptureCommand.TryParseCustomArea("-5, -4, 30, 24, 2.5", 0, out var area, out var delay));
        Assert.Equal(new PixelRect(-5, -4, 30, 24), area);
        Assert.Equal(2.5, delay);
        Assert.True(CaptureCommand.TryParseCustomArea("-5 -4 30 24", 1.75, out _, out delay));
        Assert.Equal(1.75, delay);
    }
}
