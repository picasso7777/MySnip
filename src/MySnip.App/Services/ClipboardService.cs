using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using MySnip.Core.Services;

namespace MySnip.App.Services;

internal sealed class ClipboardService : IClipboardService
{
    internal Uri? ReadImageUrl() => Retry(() => WebStickerService.FindImageUrl(Clipboard.GetDataObject()));
    public byte[]? TryGetPng()
    {
        var source = ReadStickers(includeText: false).FirstOrDefault();
        return source is null ? null : EncodePng(source.Frames[0]);
    }

    internal IReadOnlyList<StickerContent> ReadStickers(bool includeText = true) =>
        Retry<IReadOnlyList<StickerContent>>(() =>
        {
            var paths = Clipboard.ContainsFileDropList()
                ? Clipboard.GetFileDropList().Cast<string>().Where(path => StickerContent.Supports(path) && File.Exists(path)).ToArray()
                : [];
            if (paths.Length > 0) return paths.Select(StickerContent.Load).ToArray();

            var png = Clipboard.GetData("PNG");
            if (png is MemoryStream pngStream) return [new StickerContent(BitmapFactory.FromPng(pngStream.ToArray()))];
            if (png is byte[] pngData) return [new StickerContent(BitmapFactory.FromPng(pngData))];
            if (Clipboard.ContainsImage())
            {
                var bitmap = Clipboard.GetImage();
                if (bitmap is not null)
                {
                    bitmap.Freeze();
                    return [new StickerContent(bitmap)];
                }
            }
            if (!includeText) return [];
            var text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            if (StickerText.TryParseColor(text, out var color)) return [StickerText.ColorCard(color)];
            if (Clipboard.ContainsText(TextDataFormat.Html))
                return [new StickerContent(StickerText.Render(text, Clipboard.GetText(TextDataFormat.Html)))];
            return string.IsNullOrWhiteSpace(text) ? [] : [new StickerContent(StickerText.Render(text))];
        }) ?? [];

    public void SetPng(byte[] pngBytes) => SetPng(pngBytes, null);

    internal void SetPng(byte[] pngBytes, string? file)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        Retry<object?>(() =>
        {
            using var stream = new MemoryStream(pngBytes, false);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var data = new DataObject();
            data.SetImage(bitmap);
            if (file is not null) data.SetFileDropList(new StringCollection { file });
            using var png = new MemoryStream(pngBytes, false);
            data.SetData("PNG", png, autoConvert: false);
            Clipboard.SetDataObject(data, copy: true);
            return null;
        });
    }

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
