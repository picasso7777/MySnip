namespace MySnip.Core.Models;

public readonly record struct PixelPoint(int X, int Y);

public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => checked(X + Width);
    public int Bottom => checked(Y + Height);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromPoints(PixelPoint first, PixelPoint second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        return new PixelRect(left, top, Math.Abs(second.X - first.X), Math.Abs(second.Y - first.Y));
    }

    public PixelRect Normalize() => Width >= 0 && Height >= 0
        ? this
        : FromPoints(new PixelPoint(X, Y), new PixelPoint(Right, Bottom));

    public PixelRect ClampTo(PixelRect bounds)
    {
        var normalized = Normalize();
        var left = Math.Max(normalized.Left, bounds.Left);
        var top = Math.Max(normalized.Top, bounds.Top);
        var right = Math.Min(normalized.Right, bounds.Right);
        var bottom = Math.Min(normalized.Bottom, bounds.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(left, top, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    public bool Contains(PixelPoint point) =>
        point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public PixelRect MoveBy(int dx, int dy, PixelRect bounds)
    {
        if (Width > bounds.Width || Height > bounds.Height)
        {
            return ClampTo(bounds);
        }

        var x = Math.Clamp(checked(X + dx), bounds.Left, bounds.Right - Width);
        var y = Math.Clamp(checked(Y + dy), bounds.Top, bounds.Bottom - Height);
        return new PixelRect(x, y, Width, Height);
    }
}

public sealed record ScreenDescriptor(string DeviceName, PixelRect Bounds, double DpiScaleX, double DpiScaleY)
{
    public PixelPoint PhysicalToLogical(PixelPoint point) => new(
        (int)Math.Round((point.X - Bounds.X) / DpiScaleX),
        (int)Math.Round((point.Y - Bounds.Y) / DpiScaleY));

    public PixelPoint LogicalToPhysical(PixelPoint point) => new(
        Bounds.X + (int)Math.Round(point.X * DpiScaleX),
        Bounds.Y + (int)Math.Round(point.Y * DpiScaleY));
}

public static class DesktopGeometry
{
    public static PixelRect Enclose(IEnumerable<PixelRect> displays)
    {
        var bounds = displays.Where(display => !display.IsEmpty).ToArray();
        return bounds.Length == 0 ? default : PixelRect.FromPoints(
            new PixelPoint(bounds.Min(display => display.Left), bounds.Min(display => display.Top)),
            new PixelPoint(bounds.Max(display => display.Right), bounds.Max(display => display.Bottom)));
    }

    public static PixelRect DisplayAt(IEnumerable<PixelRect> displays, PixelPoint point, PixelRect fallback)
    {
        var nearest = fallback;
        var distance = double.PositiveInfinity;
        foreach (var display in displays)
        {
            if (display.IsEmpty) continue;
            if (display.Contains(point)) return display;
            var dx = point.X - (double)Math.Clamp(point.X, display.Left, display.Right - 1);
            var dy = point.Y - (double)Math.Clamp(point.Y, display.Top, display.Bottom - 1);
            var candidate = dx * dx + dy * dy;
            if (candidate >= distance) continue;
            nearest = display;
            distance = candidate;
        }
        return nearest;
    }
}

public enum ResizeHandle { Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

public enum SelectionHitTarget
{
    Outside,
    Move,
    ResizeLeft,
    ResizeTop,
    ResizeRight,
    ResizeBottom,
    ResizeTopLeft,
    ResizeTopRight,
    ResizeBottomLeft,
    ResizeBottomRight
}

public static class SelectionHitTesting
{
    public static SelectionHitTarget HitTest(PixelRect selection, PixelPoint point, int tolerance = 7)
    {
        if (tolerance < 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (selection.IsEmpty || point.X < selection.Left - tolerance || point.X > selection.Right + tolerance ||
            point.Y < selection.Top - tolerance || point.Y > selection.Bottom + tolerance)
            return SelectionHitTarget.Outside;

        var nearLeft = Math.Abs(point.X - selection.Left) <= tolerance;
        var nearRight = Math.Abs(point.X - selection.Right) <= tolerance;
        var nearTop = Math.Abs(point.Y - selection.Top) <= tolerance;
        var nearBottom = Math.Abs(point.Y - selection.Bottom) <= tolerance;
        if (nearLeft && nearTop) return SelectionHitTarget.ResizeTopLeft;
        if (nearRight && nearTop) return SelectionHitTarget.ResizeTopRight;
        if (nearLeft && nearBottom) return SelectionHitTarget.ResizeBottomLeft;
        if (nearRight && nearBottom) return SelectionHitTarget.ResizeBottomRight;
        if (nearLeft) return SelectionHitTarget.ResizeLeft;
        if (nearRight) return SelectionHitTarget.ResizeRight;
        if (nearTop) return SelectionHitTarget.ResizeTop;
        if (nearBottom) return SelectionHitTarget.ResizeBottom;
        return SelectionHitTarget.Move;
    }
}

public static class SelectionGeometry
{
    public static PixelRect Resize(PixelRect original, ResizeHandle handle, PixelPoint point, PixelRect bounds)
    {
        var left = handle is ResizeHandle.Left or ResizeHandle.TopLeft or ResizeHandle.BottomLeft ? point.X : original.Left;
        var top = handle is ResizeHandle.Top or ResizeHandle.TopLeft or ResizeHandle.TopRight ? point.Y : original.Top;
        var right = handle is ResizeHandle.Right or ResizeHandle.TopRight or ResizeHandle.BottomRight ? point.X : original.Right;
        var bottom = handle is ResizeHandle.Bottom or ResizeHandle.BottomLeft or ResizeHandle.BottomRight ? point.Y : original.Bottom;
        return PixelRect.FromPoints(new PixelPoint(left, top), new PixelPoint(right, bottom)).ClampTo(bounds);
    }
}

public readonly record struct OverlayLayout(
    PixelRect PhysicalBounds,
    double DpiScaleX,
    double DpiScaleY,
    double InverseLayoutScaleX,
    double InverseLayoutScaleY,
    double WindowDipWidth,
    double WindowDipHeight)
{
    public PixelPoint CanvasToPhysical(PixelPoint canvasPoint) => new(
        checked(PhysicalBounds.X + canvasPoint.X),
        checked(PhysicalBounds.Y + canvasPoint.Y));

    public PixelPoint PhysicalToCanvas(PixelPoint physicalPoint) => new(
        checked(physicalPoint.X - PhysicalBounds.X),
        checked(physicalPoint.Y - PhysicalBounds.Y));
}

public static class OverlayCoordinateMapper
{
    public static OverlayLayout Create(PixelRect physicalBounds, uint dpiX, uint dpiY)
    {
        if (physicalBounds.IsEmpty) throw new ArgumentOutOfRangeException(nameof(physicalBounds));
        if (dpiX == 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
        if (dpiY == 0) throw new ArgumentOutOfRangeException(nameof(dpiY));

        var scaleX = dpiX / 96d;
        var scaleY = dpiY / 96d;
        return new OverlayLayout(
            physicalBounds,
            scaleX,
            scaleY,
            1d / scaleX,
            1d / scaleY,
            physicalBounds.Width / scaleX,
            physicalBounds.Height / scaleY);
    }
}
