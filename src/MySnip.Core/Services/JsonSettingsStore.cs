using System.Text.Json;
using MySnip.Core.Models;

namespace MySnip.Core.Services;

public class JsonSettingsStore(string settingsPath) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public string SettingsPath { get; } = Path.GetFullPath(settingsPath);

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
            return loaded is { SchemaVersion: 1 } ? SettingsValidator.Normalize(loaded) : new AppSettings();
        }
        catch (JsonException)
        {
            BackupCorruptFile();
            return new AppSettings();
        }
        catch (IOException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(SettingsValidator.Normalize(settings), Options));
        File.Move(temporary, SettingsPath, true);
    }

    private void BackupCorruptFile()
    {
        try { File.Move(SettingsPath, SettingsPath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); }
        catch (IOException) { }
    }
}
