using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MySnip.App.Services;

internal static class CaptureOutput
{
    public static void Print(BitmapSource image, PrintDialog dialog)
    {
        var visual = new DrawingVisual();
        var scale = Math.Min(dialog.PrintableAreaWidth / image.PixelWidth, dialog.PrintableAreaHeight / image.PixelHeight);
        using (var drawing = visual.RenderOpen())
            drawing.DrawImage(image, new Rect(0, 0, image.PixelWidth * scale, image.PixelHeight * scale));
        dialog.PrintVisual(visual, "MySnip");
    }

    public static string QuickSave(BitmapSource image, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"MySnip-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.png");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return path;
    }

    public static BitmapSource Decorate(BitmapSource image, bool rounded, bool border, bool shadow)
    {
        if (!rounded && !border && !shadow) return image;
        var padding = shadow ? 16 : border ? 1 : 0;
        var width = image.PixelWidth;
        var height = image.PixelHeight;
        var bounds = new Rect(padding, padding, width, height);
        var radius = rounded ? Math.Min(12, Math.Min(width, height) / 2d) : 0;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            if (shadow)
            {
                for (var spread = 10; spread > 0; spread--)
                {
                    var shadowBounds = bounds;
                    shadowBounds.Offset(0, 4);
                    shadowBounds.Inflate(spread, spread);
                    drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(15, 0, 0, 0)), null,
                        shadowBounds, radius + spread, radius + spread);
                }
            }
            drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
            drawing.DrawImage(image, bounds);
            drawing.Pop();
            if (border) drawing.DrawRoundedRectangle(null, new Pen(Brushes.DimGray, 1), bounds, radius, radius);
        }
        var result = new RenderTargetBitmap(width + 2 * padding, height + 2 * padding, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
