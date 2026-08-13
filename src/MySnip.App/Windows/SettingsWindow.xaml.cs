using System.Windows;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class SettingsWindow : Window
{
    private static readonly string[] FunctionKeys = Enumerable.Range(1, 12).Select(i => $"F{i}").ToArray();

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        CaptureKey.ItemsSource = FunctionKeys;
        PasteKey.ItemsSource = FunctionKeys;
        CaptureKey.SelectedItem = ToName(settings.CaptureHotkey.VirtualKey);
        PasteKey.SelectedItem = ToName(settings.PasteHotkey.VirtualKey);
        OutputDirectory.Text = settings.OutputDirectory;
        StartWithWindows.IsChecked = settings.StartWithWindows;
        ToolColor.SelectedIndex = 0;
        foreach (System.Windows.Controls.ComboBoxItem item in ToolColor.Items)
            if (string.Equals(item.Tag as string, settings.Tools.Color, StringComparison.OrdinalIgnoreCase)) ToolColor.SelectedItem = item;
        ToolWidth.SelectedIndex = 1;
        foreach (System.Windows.Controls.ComboBoxItem item in ToolWidth.Items)
            if (double.TryParse(item.Content?.ToString(), out var width) && Math.Abs(width - settings.Tools.StrokeWidth) < 0.01) ToolWidth.SelectedItem = item;
        ToolOpacity.Value = Math.Clamp(settings.Tools.Opacity, 0.1, 1);
    }

    public AppSettings? Result { get; private set; }

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
        if (CaptureKey.SelectedItem is not string capture || PasteKey.SelectedItem is not string paste) return;
        if (capture == paste)
        {
            MessageBox.Show(this, "兩個功能不能使用相同的快捷鍵。", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputDirectory.Text))
        {
            MessageBox.Show(this, "請選擇輸出資料夾。", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var color = (ToolColor.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "#FFFF3B30";
        _ = double.TryParse((ToolWidth.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString(), out var strokeWidth);
        Result = new AppSettings
        {
            CaptureHotkey = new HotkeyGesture(ToVirtualKey(capture)),
            PasteHotkey = new HotkeyGesture(ToVirtualKey(paste)),
            OutputDirectory = Path.GetFullPath(OutputDirectory.Text.Trim()),
            Tools = new ToolDefaults { Color = color, StrokeWidth = strokeWidth <= 0 ? 3 : strokeWidth, Opacity = ToolOpacity.Value },
            StartWithWindows = StartWithWindows.IsChecked == true
        };
        DialogResult = true;
    }

    private static string ToName(uint virtualKey) => $"F{virtualKey - 0x6F}";
    private static uint ToVirtualKey(string keyName) => 0x6F + uint.Parse(keyName.AsSpan(1));
}
