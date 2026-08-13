using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal static class BitmapFactory
{
    public static BitmapSource FromFrame(CapturedFrame frame)
    {
        var bitmap = BitmapSource.Create(frame.Bounds.Width, frame.Bounds.Height, 96, 96,
            PixelFormats.Pbgra32, null, frame.BgraPixels, frame.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource FromPng(byte[] png)
    {
        using var stream = new MemoryStream(png, false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static byte[] Encode(BitmapSource source, string extension)
    {
        BitmapEncoder encoder = extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
