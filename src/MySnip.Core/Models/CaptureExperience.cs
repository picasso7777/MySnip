namespace MySnip.Core.Models;

public static class CaptureCandidatePicker
{
    private const int MinimumSide = 6;

    public static CaptureCandidate? PickBestCompact(
        IEnumerable<CaptureCandidate> candidates,
        PixelPoint point,
        PixelRect desktopBounds,
        PixelRect screenBounds,
        double maximumScreenAreaRatio = 0.5)
    {
        if (maximumScreenAreaRatio <= 0 || maximumScreenAreaRatio > 1)
            throw new ArgumentOutOfRangeException(nameof(maximumScreenAreaRatio));

        var materialized = candidates as IReadOnlyCollection<CaptureCandidate> ?? candidates.ToArray();
        var uiElement = PickBest(materialized, point, desktopBounds, CaptureCandidateKind.UiElement);
        if (uiElement is not null && IsCompact(uiElement.Value.Bounds)) return uiElement;

        var window = PickBest(materialized, point, desktopBounds, CaptureCandidateKind.TopLevelWindow);
        return window is not null && IsCompact(window.Value.Bounds) ? window : null;

        bool IsCompact(PixelRect bounds)
        {
            var visible = bounds.ClampTo(screenBounds);
            if (visible.Width < MinimumSide || visible.Height < MinimumSide) return false;
            var area = (long)visible.Width * visible.Height;
            var screenArea = (long)screenBounds.Width * screenBounds.Height;
            return area <= screenArea * maximumScreenAreaRatio;
        }
    }

    public static CaptureCandidate? PickBest(
        IEnumerable<CaptureCandidate> candidates,
        PixelPoint point,
        PixelRect desktopBounds,
        CaptureCandidateKind? requiredKind = null)
    {
        var normalized = candidates
            .Select(candidate => candidate with { Bounds = candidate.Bounds.ClampTo(desktopBounds) })
            .Where(candidate => candidate.Bounds.Width >= MinimumSide &&
                candidate.Bounds.Height >= MinimumSide && candidate.Bounds.Contains(point))
            .ToArray();

        var frontLayer = normalized
            .Where(candidate => candidate.Kind == CaptureCandidateKind.TopLevelWindow)
            .Select(candidate => (int?)candidate.Layer)
            .Min();
        if (frontLayer is null) return null;

        var layerCandidates = normalized.Where(candidate => candidate.Layer == frontLayer.Value);
        if (requiredKind == CaptureCandidateKind.TopLevelWindow)
            return Smallest(layerCandidates.Where(candidate => candidate.Kind == CaptureCandidateKind.TopLevelWindow));
        if (requiredKind == CaptureCandidateKind.UiElement)
            return Smallest(layerCandidates.Where(candidate => candidate.Kind == CaptureCandidateKind.UiElement));

        return Smallest(layerCandidates.Where(candidate => candidate.Kind == CaptureCandidateKind.UiElement)) ??
            Smallest(layerCandidates.Where(candidate => candidate.Kind == CaptureCandidateKind.TopLevelWindow));

        static CaptureCandidate? Smallest(IEnumerable<CaptureCandidate> source)
        {
            CaptureCandidate? best = null;
            long bestArea = long.MaxValue;

            foreach (var candidate in source)
            {
                var area = (long)candidate.Bounds.Width * candidate.Bounds.Height;
                if (area >= bestArea) continue;
                best = candidate;
                bestArea = area;
            }

            return best;
        }
    }
}

public readonly record struct PixelColor(byte R, byte G, byte B, byte A)
{
    public string ToRgbText() => $"RGB ({R}, {G}, {B})";
    public string ToHexText() => $"#{R:X2}{G:X2}{B:X2}";
}

public static class CapturedPixelSampler
{
    public static PixelColor Sample(CapturedFrame frame, PixelPoint globalPoint)
    {
        Validate(frame);
        var x = Math.Clamp(globalPoint.X - frame.Bounds.X, 0, frame.Bounds.Width - 1);
        var y = Math.Clamp(globalPoint.Y - frame.Bounds.Y, 0, frame.Bounds.Height - 1);
        var offset = RowOffset(frame, y) + x * 4;
        return new PixelColor(
            frame.BgraPixels[offset + 2],
            frame.BgraPixels[offset + 1],
            frame.BgraPixels[offset],
            frame.BgraPixels[offset + 3]);
    }

    public static byte[] SampleBgraSquare(CapturedFrame frame, PixelPoint globalCenter, int radius)
    {
        if (radius < 0) throw new ArgumentOutOfRangeException(nameof(radius));
        Validate(frame);
        var side = checked(radius * 2 + 1);
        var result = new byte[checked(side * side * 4)];
        var centerX = globalCenter.X - frame.Bounds.X;
        var centerY = globalCenter.Y - frame.Bounds.Y;

        for (var outputY = 0; outputY < side; outputY++)
        {
            var sourceY = Math.Clamp(centerY + outputY - radius, 0, frame.Bounds.Height - 1);
            for (var outputX = 0; outputX < side; outputX++)
            {
                var sourceX = Math.Clamp(centerX + outputX - radius, 0, frame.Bounds.Width - 1);
                var source = RowOffset(frame, sourceY) + sourceX * 4;
                var target = (outputY * side + outputX) * 4;
                frame.BgraPixels.AsSpan(source, 4).CopyTo(result.AsSpan(target, 4));
            }
        }

        return result;
    }

    private static int RowOffset(CapturedFrame frame, int y) => frame.Stride >= 0
        ? checked(y * frame.Stride)
        : checked((frame.Bounds.Height - 1 - y) * -frame.Stride);

    private static void Validate(CapturedFrame frame)
    {
        if (frame.Bounds.IsEmpty) throw new ArgumentException("Frame bounds cannot be empty.", nameof(frame));
        if (Math.Abs((long)frame.Stride) < frame.Bounds.Width * 4L)
            throw new ArgumentException("Frame stride is too small.", nameof(frame));
        if (frame.BgraPixels.LongLength < Math.Abs((long)frame.Stride) * frame.Bounds.Height)
            throw new ArgumentException("Frame pixel buffer is too small.", nameof(frame));
    }
}

public static class MagnifierPlacement
{
    public static PixelPoint Place(PixelPoint cursor, PixelSize popupSize, PixelRect canvasBounds, int gap = 24)
    {
        if (popupSize.IsEmpty) throw new ArgumentOutOfRangeException(nameof(popupSize));
        if (canvasBounds.IsEmpty) throw new ArgumentOutOfRangeException(nameof(canvasBounds));

        var right = cursor.X + gap;
        var left = cursor.X - gap - popupSize.Width;
        var below = cursor.Y + gap;
        var above = cursor.Y - gap - popupSize.Height;
        var x = right + popupSize.Width <= canvasBounds.Right ? right : left;
        var y = below + popupSize.Height <= canvasBounds.Bottom ? below : above;
        return new PixelPoint(
            Math.Clamp(x, canvasBounds.Left, Math.Max(canvasBounds.Left, canvasBounds.Right - popupSize.Width)),
            Math.Clamp(y, canvasBounds.Top, Math.Max(canvasBounds.Top, canvasBounds.Bottom - popupSize.Height)));
    }
}
