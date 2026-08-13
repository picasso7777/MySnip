using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MySnip.App.Interop;
using MySnip.App.Services;
using MySnip.Core.Models;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace MySnip.App.Windows;

public sealed partial class StickerWindow : Window
{
    private readonly BitmapSource _source;
    private readonly Action<StickerWindow> _onClosed;
    private StickerTransformState _state = new();
    private bool _clickThrough;

    public StickerWindow(BitmapSource source, Action<StickerWindow> onClosed)
    {
        InitializeComponent();
        _source = source;
        _onClosed = onClosed;
        StickerImage.Source = source;
        StickerImage.Width = Math.Min(source.PixelWidth, 640);
        StickerImage.Height = StickerImage.Width * source.PixelHeight / source.PixelWidth;
        Left = SystemParameters.WorkArea.Left + 70;
        Top = SystemParameters.WorkArea.Top + 70;
        MouseLeftButtonDown += (_, _) => { if (!_clickThrough) DragMove(); };
        MouseWheel += OnMouseWheel;
        ContextMenu = BuildContextMenu();
        Closed += (_, _) => _onClosed(this);
    }

    public bool IsClickThrough => _clickThrough;

    public void DisableClickThrough()
    {
        _clickThrough = false;
        UpdateExtendedStyle();
        Frame.BorderThickness = new Thickness(1);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            _state = _state.ChangeOpacity(e.Delta > 0 ? 0.05 : -0.05);
        else
            _state = _state.Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        ApplyTransform();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        Add("複製", CopyCurrentImage);
        Add("另存圖片…", Save);
        menu.Items.Add(new Separator());
        Add("順時針旋轉 90°", () => { _state = _state.RotateClockwise(); ApplyTransform(); });
        Add("水平翻轉", () => { _state = _state with { FlipHorizontal = !_state.FlipHorizontal }; ApplyTransform(); });
        Add("垂直翻轉", () => { _state = _state with { FlipVertical = !_state.FlipVertical }; ApplyTransform(); });
        Add("重設大小與角度", () => { _state = new(); ApplyTransform(); });
        menu.Items.Add(new Separator());
        Add("滑鼠穿透 (P)", () => ToggleClickThrough());
        Add("關閉", Close);
        return menu;

        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.P) ToggleClickThrough();
        else if (e.Key == Key.Escape) Close();
    }

    private void ApplyTransform()
    {
        Opacity = _state.Opacity;
        var scaleX = _state.Scale * (_state.FlipHorizontal ? -1 : 1);
        var scaleY = _state.Scale * (_state.FlipVertical ? -1 : 1);
        StickerImage.LayoutTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(scaleX, scaleY),
                new RotateTransform(_state.RotationDegrees)
            }
        };
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        UpdateExtendedStyle();
        Frame.BorderThickness = _clickThrough ? new Thickness(0) : new Thickness(1);
    }

    private void UpdateExtendedStyle()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var current = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var next = _clickThrough
            ? current | NativeMethods.WsExTransparent | NativeMethods.WsExLayered
            : current & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(next));
        NativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);
    }

    private void Save()
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Filter = "PNG 圖片|*.png|JPEG 圖片|*.jpg",
                FileName = $"MySnip-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllBytes(dialog.FileName, BitmapFactory.Encode(RenderCurrentImage(), Path.GetExtension(dialog.FileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"無法儲存貼圖：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCurrentImage()
    {
        try { System.Windows.Clipboard.SetImage(RenderCurrentImage()); }
        catch (System.Runtime.InteropServices.ExternalException ex)
        { MessageBox.Show(this, $"無法使用剪貼簿：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private BitmapSource RenderCurrentImage()
    {
        var transforms = new TransformGroup();
        transforms.Children.Add(new ScaleTransform(_state.FlipHorizontal ? -1 : 1, _state.FlipVertical ? -1 : 1));
        transforms.Children.Add(new RotateTransform(_state.RotationDegrees));
        if (Math.Abs(_state.Scale - 1) > 0.001)
            transforms.Children.Add(new ScaleTransform(_state.Scale, _state.Scale));
        var transformed = new TransformedBitmap(_source, transforms);
        var width = Math.Max(1, transformed.PixelWidth);
        var height = Math.Max(1, transformed.PixelHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushOpacity(_state.Opacity);
            drawing.DrawImage(transformed, new Rect(0, 0, width, height));
            drawing.Pop();
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}
