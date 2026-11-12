using System.Diagnostics;
using System.Text.Json;
using MySnip.App.Windows;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal enum StickerBackground { Transparent, Clickable, LightCheckerboard, DarkCheckerboard, SolidWhite }

internal sealed record StickerLayout(
    Guid Id, string Group, PixelPoint Position, StickerTransformState Transform,
    bool Hidden, bool ClickThrough, bool Topmost, bool Thumbnail, double ThumbnailWidth,
    bool Grayscale, bool Invert, StickerBackground Background, string? Color, string? ImageFile = null,
    Guid? DesktopId = null);

internal sealed record StickerWorkspace(string[] Groups, string ActiveGroup, bool AllHidden, StickerLayout[] Stickers);

internal sealed class StickerSession(string directory)
{
    private readonly Dictionary<Guid, (StickerContent Content, string Name)> _files = [];
    private string IndexPath => Path.Combine(directory, "stickers.json");

    public StickerWorkspace? Read()
    {
        try { return File.Exists(IndexPath) ? JsonSerializer.Deserialize<StickerWorkspace>(File.ReadAllBytes(IndexPath)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Trace.TraceWarning("Cannot read sticker session: {0}", ex.Message); return null; }
    }

    public StickerContent? Restore(StickerLayout layout)
    {
        if (layout.ImageFile is not { } name || !ValidImageFile(name)) return null;
        try
        {
            var content = StickerContent.Load(Path.Combine(directory, name)) with { Color = layout.Color };
            _files[layout.Id] = (content, name);
            return content;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Trace.TraceWarning("Cannot read sticker image: {0}", ex.Message); return null; }
    }

    public void Save(string[] groups, string activeGroup, bool allHidden, IReadOnlyList<StickerWindow> stickers)
    {
        Directory.CreateDirectory(directory);
        var current = new Dictionary<Guid, (StickerContent Content, string Name)>();
        var layouts = new List<StickerLayout>();
        var created = new List<string>();
        var temp = IndexPath + ".tmp";
        try
        {
            foreach (var sticker in stickers)
            {
                var layout = sticker.Snapshot();
                var content = sticker.ImageContent;
                if (!_files.TryGetValue(layout.Id, out var file) || !ReferenceEquals(file.Content, content) ||
                    !File.Exists(Path.Combine(directory, file.Name)))
                {
                    var name = Guid.NewGuid().ToString("N") + (content.OriginalGif is null ? ".png" : ".gif");
                    file = (content, name);
                    var path = Path.Combine(directory, name);
                    created.Add(path);
                    File.WriteAllBytes(path, content.OriginalGif ?? ClipboardService.EncodePng(content.Frames[0]));
                }
                current.Add(layout.Id, file);
                layouts.Add(layout with { ImageFile = file.Name });
            }
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new StickerWorkspace(groups, activeGroup, allHidden, layouts.ToArray())));
            File.Move(temp, IndexPath, true);
        }
        catch
        {
            foreach (var path in created) TryDelete(path);
            TryDelete(temp);
            throw;
        }
        _files.Clear();
        foreach (var file in current) _files.Add(file.Key, file.Value);
        Cleanup(current.Values.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    public void Clear()
    {
        File.Delete(IndexPath);
        _files.Clear();
        Cleanup([]);
    }

    private void Cleanup(HashSet<string> keep)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(path);
            if (ValidImageFile(name) && !keep.Contains(name)) TryDelete(path);
        }
    }

    private static bool ValidImageFile(string name) => name.Length == 36 &&
        name[32..] is ".png" or ".gif" && Guid.TryParseExact(name[..32], "N", out _);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Cannot remove sticker file: {0}", ex.Message); }
    }
}
