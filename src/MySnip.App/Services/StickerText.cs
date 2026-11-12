using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MySnip.App.Services;

internal static class StickerText
{
    public static bool TryParseColor(string text, out Color color)
    {
        color = default;
        text = text.Trim();
        if (Regex.IsMatch(text, @"^#(?:[0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase))
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        var match = Regex.Match(text, @"^rgb\s*\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*\)$", RegexOptions.IgnoreCase);
        if (!match.Success || !byte.TryParse(match.Groups[1].Value, out var r) ||
            !byte.TryParse(match.Groups[2].Value, out var g) || !byte.TryParse(match.Groups[3].Value, out var b)) return false;
        color = Color.FromRgb(r, g, b);
        return true;
    }

    public static StickerContent ColorCard(Color color)
    {
        var hex = color.A == 255 ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : color.ToString();
        var text = Format($"{hex}    RGB ({color.R}, {color.G}, {color.B})", 16, Brushes.Black);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 300, 105));
            drawing.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 300, 66));
            drawing.DrawText(text, new Point(12, 77));
        }
        return new StickerContent([Render(visual, 300, 105)], [100], Color: hex);
    }

    public static BitmapSource Render(string text, string? html = null)
    {
        List<(int Start, int Length, bool Bold, bool Italic)> styles = [];
        if (!string.IsNullOrEmpty(html))
        {
            const string startMarker = "<!--StartFragment-->";
            var start = html.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            var end = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
            if (start >= 0 && end >= start + startMarker.Length) html = html[(start + startMarker.Length)..end];
            else
            {
                var body = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
                if (body >= 0) html = html[body..];
            }
            html = Regex.Replace(html, @"<(script|style|head)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var builder = new StringBuilder();
            var bold = 0;
            var italic = 0;
            foreach (Match token in Regex.Matches(html, @"<!--.*?-->|<[^>]*>|[^<]+", RegexOptions.Singleline))
            {
                if (token.Value.StartsWith('<'))
                {
                    var tag = Regex.Match(token.Value, @"^<\s*(/?)\s*([a-z0-9]+)", RegexOptions.IgnoreCase);
                    var name = tag.Groups[2].Value.ToLowerInvariant();
                    var closing = tag.Groups[1].Length > 0;
                    if (name is "b" or "strong" or "h1" or "h2" or "h3") bold = Math.Max(0, bold + (closing ? -1 : 1));
                    if (name is "i" or "em") italic = Math.Max(0, italic + (closing ? -1 : 1));
                    if (name == "br" || closing && name is "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3")
                    {
                        if (builder.Length > 0 && builder[^1] != '\n') builder.Append('\n');
                    }
                    if (name == "li" && !closing) builder.Append("• ");
                    continue;
                }
                var value = WebUtility.HtmlDecode(Regex.Replace(token.Value, @"\s+", " "));
                if (builder.Length == 0 || builder[^1] is ' ' or '\n') value = value.TrimStart();
                if (bold > 0 || italic > 0) styles.Add((builder.Length, value.Length, bold > 0, italic > 0));
                builder.Append(value);
            }
            if (builder.Length > 0) text = builder.ToString().TrimEnd();
        }
        if (text.Length > 100_000) text = text[..100_000];
        var formatted = Format(text, 16, Brushes.Black);
        formatted.MaxTextWidth = 640;
        formatted.MaxTextHeight = 4000;
        foreach (var style in styles)
        {
            var length = Math.Min(style.Length, text.Length - style.Start);
            if (length <= 0) continue;
            if (style.Bold) formatted.SetFontWeight(FontWeights.Bold, style.Start, length);
            if (style.Italic) formatted.SetFontStyle(FontStyles.Italic, style.Start, length);
        }
        var width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 24;
        var height = (int)Math.Ceiling(formatted.Height) + 24;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawText(formatted, new Point(12, 12));
        }
        return Render(visual, width, height);
    }

    private static FormattedText Format(string text, double size, Brush foreground) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, foreground, 1);

    private static BitmapSource Render(DrawingVisual visual, int width, int height)
    {
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
