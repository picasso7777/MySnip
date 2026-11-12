using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MySnip.App.Windows;

internal static class StickerCropWindow
{
    public static BitmapSource? ShowCrop(Window owner, BitmapSource source)
    {
        var scale = Math.Min(1, Math.Min(720d / source.PixelWidth, 500d / source.PixelHeight));
        var width = source.PixelWidth * scale;
        var height = source.PixelHeight * scale;
        var surface = new Canvas { Width = width, Height = height, Background = Brushes.WhiteSmoke, ClipToBounds = true, Cursor = Cursors.Cross };
        surface.Children.Add(new Image { Source = source, Width = width, Height = height, Stretch = Stretch.Fill, IsHitTestVisible = false });
        var outline = new Rectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(40, 0, 120, 255)), IsHitTestVisible = false };
        surface.Children.Add(outline);
        var dialog = new Window
        {
            Title = "裁切貼圖", Owner = owner, Topmost = owner.Topmost, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight
        };
        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock { Text = "拖曳框選要保留的區域", Margin = new Thickness(0, 0, 0, 8) });
        layout.Children.Add(surface);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var accept = new Button { Content = "裁切", IsDefault = true, IsEnabled = false };
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(new Button { Content = "取消", IsCancel = true });
        actions.Children.Add(accept);
        layout.Children.Add(actions);
        dialog.Content = layout;
        Point start = default;
        Rect selection = Rect.Empty;
        surface.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(surface); surface.CaptureMouse(); };
        surface.MouseMove += (_, e) => { if (surface.IsMouseCaptured) Update(e.GetPosition(surface)); };
        surface.MouseLeftButtonUp += (_, e) =>
        {
            if (!surface.IsMouseCaptured) return;
            Update(e.GetPosition(surface));
            surface.ReleaseMouseCapture();
        };
        if (dialog.ShowDialog() != true || selection.IsEmpty) return null;
        var left = Math.Min(source.PixelWidth - 1, (int)Math.Floor(selection.Left / scale));
        var top = Math.Min(source.PixelHeight - 1, (int)Math.Floor(selection.Top / scale));
        var right = Math.Clamp((int)Math.Ceiling(selection.Right / scale), left + 1, source.PixelWidth);
        var bottom = Math.Clamp((int)Math.Ceiling(selection.Bottom / scale), top + 1, source.PixelHeight);
        var result = new CroppedBitmap(source, new Int32Rect(left, top, right - left, bottom - top));
        result.Freeze();
        return result;

        void Update(Point end)
        {
            end = new Point(Math.Clamp(end.X, 0, width), Math.Clamp(end.Y, 0, height));
            selection = new Rect(start, end);
            Canvas.SetLeft(outline, selection.X);
            Canvas.SetTop(outline, selection.Y);
            outline.Width = selection.Width;
            outline.Height = selection.Height;
            accept.IsEnabled = selection.Width > 0 && selection.Height > 0;
        }
    }
}
