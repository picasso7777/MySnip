using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using MySnip.App.Services;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class AdvancedSettingsWindow : Window
{
    private readonly AdvancedPreferences _original;
    private readonly ObservableCollection<EditableHotkey> _hotkeys;

    public AdvancedSettingsWindow(AdvancedPreferences options)
    {
        InitializeComponent();
        _original = options;
        _hotkeys = new(options.CommandHotkeys.Select(binding => new EditableHotkey
        {
            Shortcut = ShortcutText.Format(binding.Gesture), Command = binding.Command, Exclusions = binding.IgnoreApplications
        }));
        Bindings.ItemsSource = _hotkeys;
        SuperSnip.IsChecked = options.SuperSnip;
        RecordCanceled.IsChecked = options.RecordCanceledCaptures;
        CopyFile.IsChecked = options.CopyImageAsFile;
        HideForSave.IsChecked = options.HideCaptureForSaveDialog;
        ShowMagnifier.IsChecked = options.ShowMagnifier;
        Crosshair.IsChecked = options.MagnifierCrosshair;
        MagnifierSize.Text = options.MagnifierSize.ToString();
        SampleRadius.Text = options.MagnifierSampleRadius.ToString();
        MagnifierColor.Text = options.MagnifierBackground;
        RememberColors.IsChecked = options.RememberToolColors;
        SelectNew.IsChecked = options.SelectNewAnnotation;
        PenSizes.Text = string.Join(", ", options.PenSizes);
        IgnoreApplications.Text = options.IgnoreApplications;
        Choices(SuperOutput, options.SuperSnipOutput, ["複製", "貼圖", "進入編輯"]);
        Choices(Desktop, options.StickerDesktop, ["保留在原本的虛擬桌面", "跟隨作用中的虛擬桌面"]);
        string[] escapeActions = ["隱藏貼圖", "關閉貼圖", "取消滑鼠穿透", "不執行動作"];
        Choices(Escape, options.StickerEscape, escapeActions);
        Choices(ShiftEscape, options.StickerShiftEscape, escapeActions);
        Choices(Theme, options.Theme, ["跟隨 Windows", "淺色", "深色"]);
        string[] trayActions = ["開啟選單", "開始截圖", "從剪貼簿貼圖", "顯示／隱藏貼圖", "開啟設定", "不執行動作"];
        Choices(TrayLeft, options.TrayLeft, trayActions);
        Choices(TrayMiddle, options.TrayMiddle, trayActions);
        Choices(TrayRight, options.TrayRight, trayActions);
    }

    public AdvancedPreferences? Result { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        Bindings.CommitEdit(DataGridEditingUnit.Cell, true);
        Bindings.CommitEdit(DataGridEditingUnit.Row, true);
        if (!int.TryParse(MagnifierSize.Text, out var size) || size is < 72 or > 320 ||
            !int.TryParse(SampleRadius.Text, out var radius) || radius is < 2 or > 25 ||
            !AdvancedPreferences.IsColor(MagnifierColor.Text.Trim()))
        { ShowError("請檢查放大鏡尺寸、半徑和背景色。"); return; }

        var sizes = PenSizes.Text.Split([',', '，'], StringSplitOptions.TrimEntries);
        if (sizes.Length is < 1 or > 5 || sizes.Any(value => !int.TryParse(value, out var width) || width is < 1 or > 32))
        { ShowError("請輸入 1–5 個畫筆粗細，每個數值須為 1–32。"); return; }
        var bindings = new List<CommandHotkey>();
        foreach (var row in _hotkeys)
        {
            if (string.IsNullOrWhiteSpace(row.Shortcut) && string.IsNullOrWhiteSpace(row.Command) &&
                string.IsNullOrWhiteSpace(row.Exclusions)) continue;
            if (!ShortcutText.TryParse(row.Shortcut, out var gesture) ||
                CommandLineText.Split(row.Command).Length == 0)
            { ShowError($"請檢查快捷鍵或命令「{row.Shortcut}」。"); return; }
            if (bindings.Any(binding => !HotkeyValidator.AreDistinct(binding.Gesture, gesture)))
            { ShowError($"快捷鍵「{row.Shortcut}」重複。"); return; }
            bindings.Add(new CommandHotkey(gesture, row.Command.Trim(), row.Exclusions.Trim()));
        }
        if (bindings.Count > 64) { ShowError("最多可設定 64 組命令快捷鍵。"); return; }
        Result = _original with
        {
            SuperSnip = SuperSnip.IsChecked == true, SuperSnipOutput = Selected<SuperSnipOutput>(SuperOutput),
            RecordCanceledCaptures = RecordCanceled.IsChecked == true, CopyImageAsFile = CopyFile.IsChecked == true,
            HideCaptureForSaveDialog = HideForSave.IsChecked == true,
            StickerDesktop = Selected<StickerDesktopMode>(Desktop), StickerEscape = Selected<StickerEscapeAction>(Escape),
            StickerShiftEscape = Selected<StickerEscapeAction>(ShiftEscape), Theme = Selected<AppTheme>(Theme),
            ShowMagnifier = ShowMagnifier.IsChecked == true, MagnifierCrosshair = Crosshair.IsChecked == true,
            MagnifierSize = size, MagnifierSampleRadius = radius, MagnifierBackground = MagnifierColor.Text.Trim(),
            RememberToolColors = RememberColors.IsChecked == true, SelectNewAnnotation = SelectNew.IsChecked == true,
            PenSizes = sizes.Select(int.Parse).ToArray(), TrayLeft = Selected<TrayAction>(TrayLeft),
            TrayMiddle = Selected<TrayAction>(TrayMiddle), TrayRight = Selected<TrayAction>(TrayRight),
            IgnoreApplications = IgnoreApplications.Text.Trim(), CommandHotkeys = bindings.ToArray()
        };
        DialogResult = true;
    }

    private void ShowError(string text) => MessageBox.Show(this, text, "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static void Choices<T>(ComboBox combo, T current, string[] labels) where T : struct, Enum
    {
        var values = Enum.GetValues<T>();
        combo.ItemsSource = values.Zip(labels, (value, label) => new KeyValuePair<T, string>(value, label));
        combo.DisplayMemberPath = "Value";
        combo.SelectedValuePath = "Key";
        combo.SelectedValue = current;
    }

    private static T Selected<T>(ComboBox combo) where T : struct, Enum => combo.SelectedValue is T value ? value : default;

    public sealed class EditableHotkey
    {
        public string Shortcut { get; set; } = "";
        public string Command { get; set; } = "";
        public string Exclusions { get; set; } = "";
    }
}
