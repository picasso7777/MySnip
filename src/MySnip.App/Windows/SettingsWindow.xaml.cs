using System.Windows;
using MySnip.App.Services;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class SettingsWindow : Window
{
    private static readonly string[] FunctionKeys = Enumerable.Range(1, 12).Select(i => $"F{i}").ToArray();
    private readonly AppSettings _original;
    private AdvancedPreferences _advanced;
    private static readonly Dictionary<HotCornerAction, string> CornerActions = new()
    {
        [HotCornerAction.Disabled] = "停用",
        [HotCornerAction.Capture] = "開始截圖",
        [HotCornerAction.Paste] = "從剪貼簿貼圖",
        [HotCornerAction.ToggleStickers] = "顯示／隱藏貼圖"
    };

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _original = settings;
        _advanced = settings.Advanced;
        CaptureKey.ItemsSource = FunctionKeys;
        PasteKey.ItemsSource = FunctionKeys;
        CaptureKey.Text = ShortcutText.Format(settings.CaptureHotkey);
        PasteKey.Text = ShortcutText.Format(settings.PasteHotkey);
        VisibilityKey.Text = settings.EffectiveVisibilityHotkey() is { } visibility ? ShortcutText.Format(visibility) : "";
        OutputDirectory.Text = settings.OutputDirectory;
        StartWithWindows.IsChecked = settings.StartWithWindows;
        CaptureDelay.Text = settings.CaptureDelaySeconds.ToString();
        HistoryLimit.Text = settings.CaptureHistoryLimit.ToString();
        IncludeCursor.IsChecked = settings.IncludeCursor;
        AutoSave.IsChecked = settings.AutoSaveCaptures;
        RestoreStickers.IsChecked = settings.RestoreStickers;
        BindCorner(CornerTopLeft, settings.HotCorners.TopLeft);
        BindCorner(CornerTopRight, settings.HotCorners.TopRight);
        BindCorner(CornerBottomLeft, settings.HotCorners.BottomLeft);
        BindCorner(CornerBottomRight, settings.HotCorners.BottomRight);
        ToolColor.SelectedIndex = 0;
        foreach (System.Windows.Controls.ComboBoxItem item in ToolColor.Items)
            if (string.Equals(item.Tag as string, settings.Tools.Color, StringComparison.OrdinalIgnoreCase)) ToolColor.SelectedItem = item;
        ToolWidth.SelectedIndex = 1;
        foreach (System.Windows.Controls.ComboBoxItem item in ToolWidth.Items)
            if (double.TryParse(item.Content?.ToString(), out var width) && Math.Abs(width - settings.Tools.StrokeWidth) < 0.01) ToolWidth.SelectedItem = item;
        ToolOpacity.Value = Math.Clamp(settings.Tools.Opacity, 0.1, 1);
    }

    public AppSettings? Result { get; private set; }

    private void Advanced_Click(object sender, RoutedEventArgs e)
    {
        var advanced = new AdvancedSettingsWindow(_advanced) { Owner = this };
        if (advanced.ShowDialog() == true && advanced.Result is { } result) _advanced = result;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "選擇 MySnip 圖片輸出資料夾",
            UseDescriptionForTitle = true,
            SelectedPath = OutputDirectory.Text
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) OutputDirectory.Text = dialog.SelectedPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        HotkeyGesture? visibility = null;
        if (!string.IsNullOrWhiteSpace(VisibilityKey.Text) && !ShortcutText.TryParse(VisibilityKey.Text, out visibility))
        {
            MessageBox.Show(this, "請輸入有效的貼圖顯示快捷鍵，或留空停用。", "MySnip");
            return;
        }
        if (!ShortcutText.TryParse(CaptureKey.Text, out var capture) || !ShortcutText.TryParse(PasteKey.Text, out var paste))
        {
            MessageBox.Show(this, "請輸入有效快捷鍵，例如 F1 或 Ctrl+Shift+S。", "MySnip");
            return;
        }
        var standard = visibility is null ? new[] { capture, paste } : new[] { capture, paste, visibility };
        if (standard.Distinct().Count() != standard.Length || _advanced.CommandHotkeys.Any(binding =>
            standard.Contains(binding.Gesture)))
        {
            MessageBox.Show(this, "兩個功能不能使用相同的快捷鍵。", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(CaptureDelay.Text, out var delay) || delay is < 1 or > 60 ||
            !int.TryParse(HistoryLimit.Text, out var history) || history is < 0 or > 100)
        {
            MessageBox.Show(this, "延遲秒數須在 1–60 之間，歷史筆數須在 0–100 之間。", "MySnip");
            return;
        }
        if (string.IsNullOrWhiteSpace(OutputDirectory.Text))
        {
            MessageBox.Show(this, "請選擇輸出資料夾。", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var color = (ToolColor.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "#FFFF3B30";
        _ = double.TryParse((ToolWidth.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), out var strokeWidth);
        string directory;
        try { directory = Path.GetFullPath(OutputDirectory.Text.Trim()); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        { MessageBox.Show(this, $"輸出資料夾無效：{ex.Message}", "MySnip"); return; }
        Result = _original with
        {
            CaptureHotkey = capture,
            PasteHotkey = paste,
            VisibilityHotkey = visibility,
            VisibilityHotkeyDisabled = visibility is null,
            Advanced = _advanced,
            OutputDirectory = directory,
            Tools = new ToolDefaults { Color = color, StrokeWidth = strokeWidth <= 0 ? 3 : strokeWidth, Opacity = ToolOpacity.Value },
            StartWithWindows = StartWithWindows.IsChecked == true,
            CaptureDelaySeconds = delay,
            CaptureHistoryLimit = history,
            IncludeCursor = IncludeCursor.IsChecked == true,
            AutoSaveCaptures = AutoSave.IsChecked == true,
            RestoreStickers = RestoreStickers.IsChecked == true,
            HotCorners = new HotCornerSettings
            {
                TopLeft = GetCorner(CornerTopLeft), TopRight = GetCorner(CornerTopRight),
                BottomLeft = GetCorner(CornerBottomLeft), BottomRight = GetCorner(CornerBottomRight)
            }
        };
        DialogResult = true;
    }

    private static void BindCorner(System.Windows.Controls.ComboBox combo, HotCornerAction action)
    {
        combo.DisplayMemberPath = "Value";
        combo.SelectedValuePath = "Key";
        combo.ItemsSource = CornerActions;
        combo.SelectedValue = CornerActions.ContainsKey(action) ? action : HotCornerAction.Disabled;
    }

    private static HotCornerAction GetCorner(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedValue is HotCornerAction action ? action : HotCornerAction.Disabled;
}
