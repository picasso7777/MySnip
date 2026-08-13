using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using MySnip.Core.Services;

namespace MySnip.App.Services;

internal sealed class ClipboardService : IClipboardService
{
    public byte[]? TryGetPng()
    {
        return Retry(() =>
        {
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap is not null) return EncodePng(bitmap);
            }

            if (Clipboard.ContainsFileDropList())
            {
                var path = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsSupportedImage);
                if (path is not null)
                {
                    using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return EncodePng(decoder.Frames[0]);
                }
            }

            return null;
        });
    }

    public void SetPng(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        Retry<object?>(() =>
        {
            using var stream = new MemoryStream(pngBytes, false);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Clipboard.SetImage(bitmap);
            return null;
        });
    }

    private static bool IsSupportedImage(string path) =>
        File.Exists(path) && new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" }
            .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    internal static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static T? Retry<T>(Func<T?> action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { return action(); }
            catch (ExternalException ex) { last = ex; Thread.Sleep(30 * (attempt + 1)); }
        }

        throw new InvalidOperationException("剪貼簿目前正由其他程式使用，請稍後再試。", last);
    }
}
