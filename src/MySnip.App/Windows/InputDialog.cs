using System.Windows;
using System.Windows.Controls;

namespace MySnip.App.Windows;

internal static class InputDialog
{
    public static string? Show(Window? owner, string title, string label, string initial, Func<string, bool> validate, bool multiline = false)
    {
        var dialog = new Window
        {
            Owner = owner, Title = title, Width = 350, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Topmost = owner?.Topmost ?? false
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 8) };
        if (multiline)
        {
            dialog.Width = 520;
            input.MinHeight = 150;
            input.MaxHeight = 350;
            input.TextWrapping = TextWrapping.Wrap;
            input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            input.AcceptsReturn = true;
        }
        var error = new TextBlock { Text = "請輸入有效的值。", Visibility = Visibility.Collapsed };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var confirm = new Button { Content = "確定", IsDefault = true };
        confirm.Click += (_, _) =>
        {
            if (validate(input.Text.Trim())) dialog.DialogResult = true;
            else { error.Visibility = Visibility.Visible; input.Focus(); }
        };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        actions.Children.Add(confirm);
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(input);
        panel.Children.Add(error);
        panel.Children.Add(actions);
        dialog.Content = panel;
        dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }
}
