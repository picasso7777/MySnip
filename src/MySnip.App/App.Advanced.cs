using System.Windows;
using MySnip.App.Interop;
using MySnip.App.Services;
using MySnip.Core.Models;
using Forms = System.Windows.Forms;

namespace MySnip.App;

public sealed partial class App
{
    private const int FirstCommandHotkeyId = 100;
    private readonly Dictionary<int, HotkeyGesture> _registeredHotkeys = [];
    private Forms.ContextMenuStrip? _trayMenu;
    private ForegroundWatcher? _foreground;
    private SuperSnipService? _superSnip;
    private volatile bool _hotkeysEnabled = true;
    private volatile bool _superSnipExcluded;
    private bool _cornersEnabled = true;
    private bool _settingsOpen;

    private void ConfigureAdvanced()
    {
        var options = _settings.Advanced;
#pragma warning disable WPF0001 // .NET 9 Fluent theme support
        ThemeMode = options.Theme switch
        {
            AppTheme.Dark => System.Windows.ThemeMode.Dark,
            AppTheme.Light => System.Windows.ThemeMode.Light,
            _ => System.Windows.ThemeMode.System
        };
#pragma warning restore WPF0001
        _stickers.ConfigurePreferences(options);
        if (_tray is not null) _tray.ContextMenuStrip = options.TrayRight == TrayAction.Menu ? _trayMenu : null;
        if (_superSnip is null && options.SuperSnip)
        {
            _superSnip = new SuperSnipService(() => _hotkeysEnabled && !_superSnipExcluded &&
                !Volatile.Read(ref _captureActive) && !Volatile.Read(ref _settingsOpen));
            _superSnip.Failed += message => ShowBalloon($"無法啟用 Win＋拖曳：{message}");
            _superSnip.Completed += region =>
            {
                var output = _settings.Advanced.SuperSnipOutput;
                var command = new CaptureCommand
                {
                    Target = CaptureTarget.Area, Region = region,
                    Outputs = output == SuperSnipOutput.Pin ? [new(CaptureDestination.Pin)] : []
                };
                _ = BeginCaptureAsync(quickCopy: output == SuperSnipOutput.Clipboard, command: command);
            };
        }
        _superSnip?.Configure(options.SuperSnip);
    }

    private void OnForegroundChanged()
    {
        _stickers.RefreshDesktop();
        RegisterHotkeys(showErrors: false);
    }

    private void SetHotkeysEnabled(bool enabled)
    {
        _hotkeysEnabled = enabled;
        RegisterHotkeys(showErrors: enabled);
    }

    private void SetCornersEnabled(bool enabled)
    {
        _cornersEnabled = enabled;
        _corners.Configure(enabled ? _settings.HotCorners : new HotCornerSettings());
    }

    private void OnTrayClick(object? sender, Forms.MouseEventArgs args)
    {
        var options = _settings.Advanced;
        var action = args.Button switch
        {
            Forms.MouseButtons.Left => options.TrayLeft,
            Forms.MouseButtons.Middle => options.TrayMiddle,
            Forms.MouseButtons.Right when options.TrayRight != TrayAction.Menu => options.TrayRight,
            _ => TrayAction.None
        };
        switch (action)
        {
            case TrayAction.Menu: ShowTrayMenu(); break;
            case TrayAction.Capture: _ = BeginCaptureAsync(); break;
            case TrayAction.Paste: PasteFromClipboard(); break;
            case TrayAction.ToggleStickers: _stickers.ToggleVisibility(); break;
            case TrayAction.Settings: ShowSettings(); break;
        }
    }

    private void ShowTrayMenu() => _trayMenu?.Show(Forms.Control.MousePosition);

    private bool RegisterHotkeys(bool showErrors)
    {
        if (_hotkeys is null) return false;
        var active = NativeMethods.GetForegroundWindow();
        _superSnipExcluded = ForegroundWatcher.Matches(active, _settings.Advanced.IgnoreApplications);
        var desired = new Dictionary<int, HotkeyGesture>();
        if (_hotkeysEnabled && !_superSnipExcluded)
        {
            desired.Add(CaptureHotkeyId, _settings.CaptureHotkey);
            desired.Add(PasteHotkeyId, _settings.PasteHotkey);
            var bindings = _settings.Advanced.CommandHotkeys;
            for (var i = 0; i < bindings.Length; i++)
            {
                var binding = bindings[i];
                if (!ForegroundWatcher.Matches(active, binding.IgnoreApplications)) desired.Add(FirstCommandHotkeyId + i, binding.Gesture);
            }
            if (_settings.EffectiveVisibilityHotkey() is { } visibility &&
                desired.Values.All(gesture => HotkeyValidator.AreDistinct(gesture, visibility)))
                desired.Add(StickerVisibilityHotkeyId, visibility);
        }

        foreach (var (id, registered) in _registeredHotkeys.ToArray())
        {
            if (desired.TryGetValue(id, out var next) && next == registered) continue;
            _hotkeys.Unregister(id);
            _registeredHotkeys.Remove(id);
        }
        var unavailable = new List<string>();
        foreach (var (id, gesture) in desired)
        {
            if (_registeredHotkeys.ContainsKey(id)) continue;
            if (_hotkeys.TryRegister(id, gesture)) _registeredHotkeys.Add(id, gesture);
            else if (id != StickerVisibilityHotkeyId) unavailable.Add(ShortcutText.Format(gesture));
        }
        if (unavailable.Count == 0) return true;
        if (showErrors) MessageBox.Show($"這些全域快捷鍵無法註冊：{string.Join("、", unavailable)}", "MySnip",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void RunHotkeyCommand(int index)
    {
        var bindings = _settings.Advanced.CommandHotkeys;
        if (index < 0 || index >= bindings.Length) return;
        var binding = bindings[index];
        if (!ForegroundWatcher.Matches(NativeMethods.GetForegroundWindow(), binding.IgnoreApplications))
            _ = RunCommandAsync(new InstanceCommand(CommandLineText.Split(binding.Command), _settings.OutputDirectory));
    }

    private void SaveAnnotationPreferences(AdvancedPreferences options)
    {
        var advanced = _settings.Advanced with { Palette = options.Palette, ToolColors = options.ToolColors, PenSizes = options.PenSizes };
        _settings = _settings with { Advanced = advanced };
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowBalloon($"無法儲存標註偏好：{ex.Message}"); }
    }
}
