using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal sealed record CaptureRecord(Guid Id, DateTimeOffset CapturedAt, PixelRect Region, bool Canceled = false);

internal sealed class CaptureHistory(string directory)
{
    private string IndexPath => Path.Combine(directory, "history.json");

    public IReadOnlyList<CaptureRecord> Read()
    {
        try
        {
            if (!File.Exists(IndexPath)) return [];
            return (JsonSerializer.Deserialize<CaptureRecord[]>(File.ReadAllBytes(IndexPath)) ?? [])
                .Where(record => record is not null && record.Id != Guid.Empty &&
                    record.Region.Width > 0 && record.Region.Height > 0 &&
                    (long)record.Region.X + record.Region.Width <= int.MaxValue &&
                    (long)record.Region.Y + record.Region.Height <= int.MaxValue && File.Exists(ImagePath(record)))
                .DistinctBy(record => record.Id)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Cannot read capture history: {0}", ex.Message);
            return [];
        }
    }

    public BitmapSource LoadImage(CaptureRecord record) => BitmapFactory.FromPng(File.ReadAllBytes(ImagePath(record)));

    public void Add(PixelRect region, BitmapSource image, int limit, bool canceled = false)
    {
        if (limit <= 0) return;
        Directory.CreateDirectory(directory);
        var record = new CaptureRecord(Guid.NewGuid(), DateTimeOffset.Now, region, canceled);
        var records = new[] { record }.Concat(Read()).ToArray();
        try
        {
            File.WriteAllBytes(ImagePath(record), ClipboardService.EncodePng(image));
            SaveIndex(records.Take(limit));
        }
        catch
        {
            TryDelete(ImagePath(record));
            throw;
        }
        foreach (var expired in records.Skip(limit)) TryDelete(ImagePath(expired));
    }

    public void Trim(int limit)
    {
        var records = Read();
        if (records.Count <= limit) return;
        SaveIndex(records.Take(limit));
        foreach (var expired in records.Skip(limit)) TryDelete(ImagePath(expired));
    }

    private string ImagePath(CaptureRecord record) => Path.Combine(directory, record.Id.ToString("N") + ".png");

    private void SaveIndex(IEnumerable<CaptureRecord> records)
    {
        var temp = IndexPath + ".tmp";
        try
        {
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(records));
            File.Move(temp, IndexPath, true);
        }
        finally { TryDelete(temp); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning("Cannot remove capture history file: {0}", ex.Message); }
    }
}
