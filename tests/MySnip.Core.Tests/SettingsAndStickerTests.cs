using MySnip.Core.Models;
using MySnip.Core.Services;

namespace MySnip.Core.Tests;

public sealed class SettingsAndStickerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MySnip.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingSettings_ReturnsExpectedDefaults()
    {
        var settings = new JsonSettingsStore(Path.Combine(_directory, "settings.json")).Load();
        Assert.Equal((uint)0x70, settings.CaptureHotkey.VirtualKey);
        Assert.Equal((uint)0x72, settings.PasteHotkey.VirtualKey);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsValues()
    {
        var store = new JsonSettingsStore(Path.Combine(_directory, "settings.json"));
        store.Save(new AppSettings { OutputDirectory = @"C:\captures", StartWithWindows = true });
        var loaded = store.Load();
        Assert.Equal(@"C:\captures", loaded.OutputDirectory);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public void CorruptSettings_FallsBackAndPreservesBackup()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{not-json");
        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Single(Directory.GetFiles(_directory, "settings.json.corrupt-*"));
    }

    [Fact]
    public void StickerTransform_ClampsScaleAndOpacity()
    {
        var state = new StickerTransformState().Zoom(100).ChangeOpacity(-100).RotateClockwise();
        Assert.Equal(8, state.Scale);
        Assert.Equal(0.1, state.Opacity);
        Assert.Equal(90, state.RotationDegrees);
    }

    [Fact]
    public void UnsupportedSchema_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":99,\"StartWithWindows\":true}");
        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.False(loaded.StartWithWindows);
    }

    [Fact]
    public void SettingsPathThatIsDirectory_LoadsDefaultsOnIoFailure()
    {
        Directory.CreateDirectory(_directory);
        var loaded = new JsonSettingsStore(_directory).Load();
        Assert.Equal(HotkeyGesture.CaptureDefault, loaded.CaptureHotkey);
    }

    [Theory]
    [InlineData(0x6F, false)]
    [InlineData(0x70, true)]
    [InlineData(0x7B, true)]
    [InlineData(0x7C, false)]
    public void HotkeyValidation_OnlyAcceptsFunctionKeys(uint key, bool expected)
    {
        Assert.Equal(expected, HotkeyValidator.IsSupported(new HotkeyGesture(key)));
    }

    [Fact]
    public void HotkeyValidation_RejectsDuplicateGestures()
    {
        Assert.False(HotkeyValidator.AreDistinct(HotkeyGesture.CaptureDefault, HotkeyGesture.CaptureDefault));
        Assert.True(HotkeyValidator.AreDistinct(HotkeyGesture.CaptureDefault, HotkeyGesture.PasteDefault));
    }

    [Fact]
    public void ValidJsonWithInvalidValues_IsNormalized()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{\"SchemaVersion\":1,\"CaptureHotkey\":{\"VirtualKey\":1},\"PasteHotkey\":{\"VirtualKey\":1},\"OutputDirectory\":\"\",\"Tools\":{\"Color\":\"bad\",\"StrokeWidth\":999,\"Opacity\":-2}}");
        var loaded = new JsonSettingsStore(path).Load();
        Assert.Equal(HotkeyGesture.CaptureDefault, loaded.CaptureHotkey);
        Assert.Equal(HotkeyGesture.PasteDefault, loaded.PasteHotkey);
        Assert.Equal("#FFFF3B30", loaded.Tools.Color);
        Assert.Equal(32, loaded.Tools.StrokeWidth);
        Assert.Equal(0.1, loaded.Tools.Opacity);
        Assert.NotEmpty(loaded.OutputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
