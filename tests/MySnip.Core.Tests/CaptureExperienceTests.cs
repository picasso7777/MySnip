using MySnip.Core.Models;

namespace MySnip.Core.Tests;

public sealed class CaptureExperienceTests
{
    [Fact]
    public void CandidatePicker_PrefersSmallestUiElementThenWindowFallback()
    {
        var desktop = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(new PixelRect(0, 0, 800, 600), CaptureCandidateKind.TopLevelWindow),
            new(new PixelRect(100, 100, 300, 200), CaptureCandidateKind.UiElement),
            new(new PixelRect(140, 120, 80, 40), CaptureCandidateKind.UiElement)
        ];

        var element = CaptureCandidatePicker.PickBest(candidates, new PixelPoint(150, 130), desktop);
        var window = CaptureCandidatePicker.PickBest(candidates, new PixelPoint(500, 300), desktop);

        Assert.Equal(new PixelRect(140, 120, 80, 40), element?.Bounds);
        Assert.Equal(CaptureCandidateKind.TopLevelWindow, window?.Kind);
    }

    [Fact]
    public void CandidatePicker_ReturnsNullWhenNothingContainsPoint()
    {
        var candidates = new[] { new CaptureCandidate(new PixelRect(10, 10, 50, 50), CaptureCandidateKind.UiElement) };
        Assert.Null(CaptureCandidatePicker.PickBest(candidates, new PixelPoint(90, 90), new PixelRect(0, 0, 100, 100)));
    }

    [Fact]
    public void CompactPicker_IgnoresCandidatesLargerThanHalfTheScreen()
    {
        var screen = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(screen, CaptureCandidateKind.TopLevelWindow),
            new(new PixelRect(50, 50, 900, 700), CaptureCandidateKind.UiElement)
        ];

        var result = CaptureCandidatePicker.PickBestCompact(
            candidates, new PixelPoint(500, 400), screen, screen);

        Assert.Null(result);
    }

    [Fact]
    public void CompactPicker_PrefersSmallestUiElementUnderCursor()
    {
        var screen = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(screen, CaptureCandidateKind.TopLevelWindow),
            new(new PixelRect(100, 100, 600, 500), CaptureCandidateKind.UiElement),
            new(new PixelRect(300, 250, 180, 90), CaptureCandidateKind.UiElement)
        ];

        var result = CaptureCandidatePicker.PickBestCompact(
            candidates, new PixelPoint(350, 300), screen, screen);

        Assert.Equal(new PixelRect(300, 250, 180, 90), result?.Bounds);
    }

    [Fact]
    public void CompactPicker_UsesSmallWindowWhenNoUiElementIsAvailable()
    {
        var screen = new PixelRect(0, 0, 1000, 800);
        var window = new CaptureCandidate(new PixelRect(100, 100, 500, 500), CaptureCandidateKind.TopLevelWindow);

        var result = CaptureCandidatePicker.PickBestCompact(
            [window], new PixelPoint(300, 300), screen, screen);

        Assert.Equal(window, result);
    }

    [Fact]
    public void CandidatePicker_BackWindowSmallElementCannotBeatFrontWindow()
    {
        var desktop = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(new PixelRect(100, 100, 500, 400), CaptureCandidateKind.TopLevelWindow, 0),
            new(new PixelRect(150, 150, 300, 200), CaptureCandidateKind.UiElement, 0),
            new(new PixelRect(0, 0, 800, 600), CaptureCandidateKind.TopLevelWindow, 1),
            new(new PixelRect(195, 195, 10, 10), CaptureCandidateKind.UiElement, 1)
        ];

        var result = CaptureCandidatePicker.PickBest(candidates, new PixelPoint(200, 200), desktop);

        Assert.Equal(new PixelRect(150, 150, 300, 200), result?.Bounds);
        Assert.Equal(0, result?.Layer);
    }

    [Fact]
    public void CandidatePicker_FrontWindowFallsBackWithoutUiAtPoint()
    {
        var desktop = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(new PixelRect(100, 100, 500, 400), CaptureCandidateKind.TopLevelWindow, 0),
            new(new PixelRect(110, 110, 50, 50), CaptureCandidateKind.UiElement, 0),
            new(new PixelRect(0, 0, 800, 600), CaptureCandidateKind.TopLevelWindow, 1),
            new(new PixelRect(195, 195, 10, 10), CaptureCandidateKind.UiElement, 1)
        ];

        var result = CaptureCandidatePicker.PickBest(candidates, new PixelPoint(200, 200), desktop);

        Assert.Equal(CaptureCandidateKind.TopLevelWindow, result?.Kind);
        Assert.Equal(0, result?.Layer);
    }

    [Fact]
    public void CandidatePicker_UsesBackWindowWhereFrontWindowDoesNotCoverPoint()
    {
        var desktop = new PixelRect(0, 0, 1000, 800);
        CaptureCandidate[] candidates =
        [
            new(new PixelRect(100, 100, 100, 100), CaptureCandidateKind.TopLevelWindow, 0),
            new(new PixelRect(0, 0, 800, 600), CaptureCandidateKind.TopLevelWindow, 1),
            new(new PixelRect(300, 300, 40, 40), CaptureCandidateKind.UiElement, 1)
        ];

        var result = CaptureCandidatePicker.PickBest(candidates, new PixelPoint(320, 320), desktop);

        Assert.Equal(new PixelRect(300, 300, 40, 40), result?.Bounds);
        Assert.Equal(1, result?.Layer);
    }

    [Fact]
    public void PixelSampler_ReadsBgraAsRgbAndClampsAtFrameEdge()
    {
        var frame = new CapturedFrame(new PixelRect(-1, -1, 2, 2), 8,
        [
            1, 2, 3, 255, 4, 5, 6, 255,
            7, 8, 9, 255, 10, 11, 12, 255
        ]);

        Assert.Equal(new PixelColor(3, 2, 1, 255), CapturedPixelSampler.Sample(frame, new PixelPoint(-10, -10)));
        Assert.Equal(new PixelColor(12, 11, 10, 255), CapturedPixelSampler.Sample(frame, new PixelPoint(99, 99)));
        Assert.Equal("RGB (12, 11, 10)", CapturedPixelSampler.Sample(frame, new PixelPoint(0, 0)).ToRgbText());
        Assert.Equal("#0C0B0A", CapturedPixelSampler.Sample(frame, new PixelPoint(0, 0)).ToHexText());
    }

    [Fact]
    public void PixelSampler_PadsNeighborhoodByRepeatingNearestEdgePixel()
    {
        var frame = new CapturedFrame(new PixelRect(0, 0, 1, 1), 4, [7, 8, 9, 255]);
        var square = CapturedPixelSampler.SampleBgraSquare(frame, new PixelPoint(0, 0), 1);

        Assert.Equal(36, square.Length);
        for (var index = 0; index < square.Length; index += 4)
            Assert.Equal(new byte[] { 7, 8, 9, 255 }, square[index..(index + 4)]);
    }

    [Fact]
    public void MagnifierPlacement_FlipsInsideBottomRightEdge()
    {
        var result = MagnifierPlacement.Place(new PixelPoint(990, 790), new PixelSize(150, 180), new PixelRect(0, 0, 1000, 800));
        Assert.Equal(new PixelPoint(816, 586), result);
    }

    [Theory]
    [InlineData(100, 150, SelectionHitTarget.ResizeLeft)]
    [InlineData(200, 150, SelectionHitTarget.ResizeRight)]
    [InlineData(150, 100, SelectionHitTarget.ResizeTop)]
    [InlineData(150, 200, SelectionHitTarget.ResizeBottom)]
    [InlineData(96, 96, SelectionHitTarget.ResizeTopLeft)]
    [InlineData(204, 96, SelectionHitTarget.ResizeTopRight)]
    [InlineData(96, 204, SelectionHitTarget.ResizeBottomLeft)]
    [InlineData(204, 204, SelectionHitTarget.ResizeBottomRight)]
    [InlineData(150, 150, SelectionHitTarget.Move)]
    [InlineData(80, 80, SelectionHitTarget.Outside)]
    public void SelectionHitTesting_IncludesVisualHandleAreaAndExclusiveEdges(int x, int y, SelectionHitTarget expected)
    {
        var result = SelectionHitTesting.HitTest(new PixelRect(100, 100, 100, 100), new PixelPoint(x, y));
        Assert.Equal(expected, result);
    }
}
