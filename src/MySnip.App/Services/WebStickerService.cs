using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;

namespace MySnip.App.Services;

internal static class WebStickerService
{
    private const int MaximumBytes = 24 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static bool TryParse(string? text, out Uri uri)
    {
        if (Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var candidate) &&
            candidate.Scheme is "http" or "https" && candidate.UserInfo.Length == 0)
        {
            uri = candidate;
            return true;
        }
        uri = null!;
        return false;
    }

    public static Uri? FindImageUrl(IDataObject? data)
    {
        if (data is null || data.GetDataPresent(DataFormats.Bitmap) || data.GetDataPresent("PNG", false) ||
            data.GetDataPresent(DataFormats.FileDrop)) return null;
        var text = data.GetData(DataFormats.UnicodeText) as string;
        if (TryParse(text, out var uri) && StickerContent.Supports(uri.AbsolutePath)) return uri;
        if (data.GetData(DataFormats.Html) is not string html) return null;
        var match = Regex.Match(html, """<img\b[^>]*\bsrc\s*=\s*["']([^"']+)["']""", RegexOptions.IgnoreCase);
        return match.Success && TryParse(WebUtility.HtmlDecode(match.Groups[1].Value), out uri) ? uri : null;
    }

    public static async Task<StickerContent> DownloadAsync(Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("圖片超過 24 MiB。");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0)
        {
            if (bytes.Length + read > MaximumBytes) throw new InvalidDataException("圖片超過 24 MiB。");
            bytes.Write(buffer, 0, read);
        }
        bytes.Position = 0;
        return StickerContent.Load(bytes, Path.GetExtension(uri.AbsolutePath));
    }
}
