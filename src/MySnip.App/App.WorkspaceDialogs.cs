using System.Windows;
using System.Windows.Controls;
using MySnip.App.Services;

namespace MySnip.App;

public sealed partial class App
{
    private void ShowCaptureHistory()
    {
        var (window, list, buttons) = WorkspaceDialog("截圖歷史", 510);
        var records = _history.Read();
        list.ItemsSource = records.Select(record => $"{record.CapturedAt:MM/dd HH:mm:ss}　{record.Region.Width} × {record.Region.Height}" +
            (record.Canceled ? "（已取消）" : "")).ToArray();
        list.SelectedIndex = records.Count > 0 ? 0 : -1;
        AddWorkspaceButton(buttons, "貼到桌面", () => UseRecord(record => _stickers.Create(_history.LoadImage(record))));
        AddWorkspaceButton(buttons, "複製", () => UseRecord(record => _clipboard.SetPng(ClipboardService.EncodePng(_history.LoadImage(record)))));
        AddWorkspaceButton(buttons, "重截範圍", () => UseRecord(record =>
        {
            window.Close();
            _ = BeginCaptureAsync(region: record.Region);
        }));
        AddWorkspaceButton(buttons, "清除歷史", () => { _history.Trim(0); window.Close(); });
        window.Show();

        void UseRecord(Action<CaptureRecord> use)
        {
            if (list.SelectedIndex >= 0) use(records[list.SelectedIndex]);
        }
    }

    private void ShowGroupManager()
    {
        var (window, list, buttons) = WorkspaceDialog("貼圖群組", 560);
        Refresh();
        AddWorkspaceButton(buttons, "切換", Select);
        AddWorkspaceButton(buttons, "新增", () => { EditGroupName(false, window); Refresh(); });
        AddWorkspaceButton(buttons, "重新命名", () => { Select(); EditGroupName(true, window); Refresh(); });
        AddWorkspaceButton(buttons, "移除", () => { Select(); _stickers.DeleteGroup(); Refresh(); });
        AddWorkspaceButton(buttons, "排序", () => { _stickers.SortGroups(); Refresh(); });
        AddWorkspaceButton(buttons, "移至目前桌面", () => { Select(); _stickers.BringGroupToCurrentDesktop(); });
        window.Show();

        void Select()
        {
            if (list.SelectedItem is string name) _stickers.SwitchGroup(name);
        }

        void Refresh()
        {
            list.ItemsSource = _stickers.Groups.ToArray();
            list.SelectedItem = _stickers.ActiveGroup;
        }
    }

    private static (Window Window, ListBox List, WrapPanel Buttons) WorkspaceDialog(string title, double width)
    {
        var layout = new DockPanel { Margin = new Thickness(12) };
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);
        var list = new ListBox();
        layout.Children.Add(list);
        var window = new Window
        {
            Title = title, Content = layout, Width = width, Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        return (window, list, buttons);
    }

    private void AddWorkspaceButton(WrapPanel panel, string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2, 8, 2, 0) };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { ShowBalloon(ex.Message); }
        };
        panel.Children.Add(button);
    }
}
