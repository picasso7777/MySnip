using MySnip.Core.Models;

namespace MySnip.Core.Tests;

public sealed class DesktopGeometryTests
{
    private static readonly PixelRect Left = new(-1600, -200, 1600, 900);
    private static readonly PixelRect Primary = new(0, 0, 1920, 1080);
    private static readonly PixelRect Above = new(1920, -1200, 1920, 1200);

    [Fact]
    public void VirtualDesktopEnclosesDisplaysWithNegativeAndOffsetOrigins()
    {
        Assert.Equal(new PixelRect(-1600, -1200, 5440, 2280), DesktopGeometry.Enclose([Left, Primary, Above]));
    }

    [Fact]
    public void DisplaySelectionUsesPhysicalCoordinatesAndFindsNearestDisplayInAGap()
    {
        PixelRect[] displays = [Left, Primary, Above];
        var desktop = DesktopGeometry.Enclose(displays);
        Assert.Equal(Left, DesktopGeometry.DisplayAt(displays, new PixelPoint(-1, 20), desktop));
        Assert.Equal(Primary, DesktopGeometry.DisplayAt(displays, new PixelPoint(0, 20), desktop));
        Assert.Equal(Above, DesktopGeometry.DisplayAt(displays, new PixelPoint(1910, -1000), desktop));
    }

    [Fact]
    public void AcrossMonitorRegionRoundTripsThroughOverlayCoordinates()
    {
        var desktop = DesktopGeometry.Enclose([Left, Primary, Above]);
        var layout = OverlayCoordinateMapper.Create(desktop, 144, 144);
        var start = new PixelPoint(-15, 10);
        var end = new PixelPoint(25, 50);
        var local = PixelRect.FromPoints(layout.PhysicalToCanvas(start), layout.PhysicalToCanvas(end));
        Assert.Equal(new PixelRect(1585, 1210, 40, 40), local);
        Assert.Equal(start, layout.CanvasToPhysical(new PixelPoint(local.Left, local.Top)));
        Assert.Equal(end, layout.CanvasToPhysical(new PixelPoint(local.Right, local.Bottom)));
    }
}

public sealed class GeometryTests
{
    [Fact]
    public void FromPoints_NormalizesReverseDrag()
    {
        var result = PixelRect.FromPoints(new PixelPoint(100, 80), new PixelPoint(-20, -10));
        Assert.Equal(new PixelRect(-20, -10, 120, 90), result);
    }

    [Fact]
    public void ClampTo_HandlesNegativeVirtualDesktopCoordinates()
    {
        var desktop = new PixelRect(-1920, -200, 4480, 1640);
        var result = new PixelRect(-2200, -100, 500, 600).ClampTo(desktop);
        Assert.Equal(new PixelRect(-1920, -100, 220, 600), result);
    }

    [Fact]
    public void MoveBy_StaysInsideBounds()
    {
        var result = new PixelRect(50, 50, 100, 100).MoveBy(1000, -1000, new PixelRect(0, 0, 400, 300));
        Assert.Equal(new PixelRect(300, 0, 100, 100), result);
    }

    [Fact]
    public void ScreenDescriptor_ConvertsUsingDpiScale()
    {
        var screen = new ScreenDescriptor("secondary", new PixelRect(-2560, 0, 2560, 1440), 1.5, 1.5);
        Assert.Equal(new PixelPoint(-2410, 150), screen.LogicalToPhysical(new PixelPoint(100, 100)));
        Assert.Equal(new PixelPoint(100, 100), screen.PhysicalToLogical(new PixelPoint(-2410, 150)));
    }

    [Theory]
    [InlineData(4, 0, 0, 150, 150)]
    [InlineData(7, 250, 250, 200, 200)]
    public void ResizeCorners_ChangesBothAxes(int handle, int x, int y, int expectedWidth, int expectedHeight)
    {
        var original = new PixelRect(50, 50, 100, 100);
        var resized = SelectionGeometry.Resize(original, (ResizeHandle)handle, new PixelPoint(x, y), new PixelRect(0, 0, 400, 400));
        Assert.Equal(expectedWidth, resized.Width);
        Assert.Equal(expectedHeight, resized.Height);
    }

    [Fact]
    public void ResizeCorner_ClampsToDesktop()
    {
        var resized = SelectionGeometry.Resize(new PixelRect(20, 20, 100, 100), ResizeHandle.TopLeft,
            new PixelPoint(-100, -100), new PixelRect(0, 0, 200, 200));
        Assert.Equal(new PixelRect(0, 0, 120, 120), resized);
    }

    [Theory]
    [InlineData(96, 1.0, 1.0, 3840, 2160)]
    [InlineData(144, 1.5, 0.6666666666666666, 2560, 1440)]
    [InlineData(192, 2.0, 0.5, 1920, 1080)]
    public void OverlayLayout_CancelsWpfDpiScale(uint dpi, double expectedDpiScale,
        double expectedInverseScale, double expectedDipWidth, double expectedDipHeight)
    {
        var layout = OverlayCoordinateMapper.Create(new PixelRect(0, 0, 3840, 2160), dpi, dpi);
        Assert.Equal(expectedDpiScale, layout.DpiScaleX, 10);
        Assert.Equal(expectedInverseScale, layout.InverseLayoutScaleX, 10);
        Assert.Equal(expectedDipWidth, layout.WindowDipWidth, 10);
        Assert.Equal(expectedDipHeight, layout.WindowDipHeight, 10);
        Assert.Equal(1, layout.InverseLayoutScaleX * layout.DpiScaleX, 10);
    }

    [Fact]
    public void OverlayLayout_PreservesNegativeVirtualDesktopOrigin()
    {
        var bounds = new PixelRect(-2560, -200, 6400, 2360);
        var layout = OverlayCoordinateMapper.Create(bounds, 144, 192);
        Assert.Equal(bounds, layout.PhysicalBounds);
        Assert.Equal(new PixelPoint(-2560, -200), layout.CanvasToPhysical(new PixelPoint(0, 0)));
        Assert.Equal(new PixelPoint(6400, 2360), layout.PhysicalToCanvas(new PixelPoint(3840, 2160)));
        Assert.Equal(2d / 3d, layout.InverseLayoutScaleX, 10);
        Assert.Equal(0.5, layout.InverseLayoutScaleY, 10);
    }

    [Theory]
    [InlineData(0, 96)]
    [InlineData(96, 0)]
    public void OverlayLayout_RejectsInvalidDpi(uint dpiX, uint dpiY)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OverlayCoordinateMapper.Create(new PixelRect(0, 0, 100, 100), dpiX, dpiY));
    }
}
