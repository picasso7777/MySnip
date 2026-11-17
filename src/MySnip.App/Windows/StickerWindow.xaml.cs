using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MySnip.App.Interop;
using MySnip.App.Services;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class StickerWindow : Window
{
    private readonly Guid _id;
    private readonly StickerManager _manager;
    private readonly DispatcherTimer _animation = new();
    private StickerContent _content;
    private BitmapSource _displayed;
    private StickerTransformState _state;
    private StickerBackground _background;
    private PixelPoint _position;
    private bool _positioned;
    private bool _selected;
    private bool _clickThrough;
    private bool _thumbnail;
    private bool _grayscale;
    private bool _invert;
    private bool _playing = true;
    private double _thumbnailWidth = 160;
    private double _speed = 1;
    private int _frameIndex;
    private Point? _cropStart;
    private Point? _dragStart;
    private bool _nativeSizeUpdatePending;
    private WindowsShare? _share;

    internal StickerWindow(StickerContent content, StickerManager manager, StickerLayout? layout = null)
    {
        InitializeComponent();
        _manager = manager;
        _content = content;
        _displayed = content.Frames[0];
        _id = layout?.Id ?? Guid.NewGuid();
        Group = layout?.Group ?? "";
        DesktopId = layout?.DesktopId;
        UserHidden = layout?.Hidden ?? false;
        _clickThrough = layout?.ClickThrough ?? false;
        _thumbnail = layout?.Thumbnail ?? false;
        _thumbnailWidth = Math.Clamp(layout?.ThumbnailWidth ?? 160, 40, 1000);
        _grayscale = layout?.Grayscale ?? false;
        _invert = layout?.Invert ?? false;
        _background = layout?.Background ?? StickerBackground.Transparent;
        Topmost = layout?.Topmost ?? true;
        _state = Normalize(layout?.Transform ?? new());
        var cursor = System.Windows.Forms.Control.MousePosition;
        _position = layout?.Position ?? new PixelPoint(cursor.X + 12, cursor.Y + 12);
        ShowActivated = false;
        _animation.Tick += (_, _) => AdvanceFrame(1);
        SourceInitialized += (_, _) =>
        {
            SetImageSize(VisualTreeHelper.GetDpi(this));
            ApplyClickThrough();
        };
        DpiChanged += (_, e) => SetImageSize(e.NewDpi);
        Frame.MouseLeftButtonDown += OnStickerMouseDown;
        MouseLeftButtonUp += (_, _) => CancelPendingDrag();
        Frame.MouseWheel += OnStickerMouseWheel;
        Frame.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (_content.Frames.Length == 1)
            {
                _cropStart = e.GetPosition(Frame);
                Mouse.Capture(Frame);
            }
        };
        Frame.PreviewMouseRightButtonUp += OnStickerRightButtonUp;
        LostMouseCapture += (_, _) => { _cropStart = null; _dragStart = null; };
        MouseMove += OnStickerMouseMove;
        MouseLeave += (_, _) => ColorSample.Visibility = Visibility.Collapsed;
        LocationChanged += (_, _) =>
        {
            if (!_positioned || !IsVisible) return;
            _position = GetPosition();
            _manager.Changed();
        };
        ContextMenu = new ContextMenu();
        ContextMenuOpening += (_, e) =>
        {
            if (e.CursorLeft >= 0 && !Frame.IsMouseOver) { e.Handled = true; return; }
            PopulateContextMenu();
        };
        AllowDrop = true;
        Drop += (_, e) => _manager.Drop?.Invoke(this, e.Data);
        Closed += (_, _) => { _animation.Stop(); _share?.Dispose(); _manager.Remove(this); };
        RefreshFrame();
        ApplyState();
    }

    internal string Group { get; set; }
    internal Guid? DesktopId { get; set; }
    internal nint WindowHandle => new WindowInteropHelper(this).Handle;
    internal bool IsClickThrough => _clickThrough;
    internal bool ContainsScreenPoint(int x, int y)
    {
        if (WindowHandle == 0 || !IsVisible) return false;
        return new Rect(Frame.PointToScreen(new Point()), Frame.PointToScreen(new Point(Frame.ActualWidth, Frame.ActualHeight)))
            .Contains(x, y);
    }
    internal bool UserHidden { get; set; }
    internal StickerContent ImageContent => _content;
    internal StickerLayout Snapshot() => new(_id, Group, _position, _state,
        UserHidden, _clickThrough, Topmost, _thumbnail, _thumbnailWidth, _grayscale, _invert, _background, _content.Color,
        DesktopId: DesktopId);

    internal void SetInitialPosition(PixelPoint position) => _position = position;

    internal void SetShown(bool visible)
    {
        if (visible == IsVisible) return;
        if (!visible) { Hide(); _animation.Stop(); return; }
        Show();
        if (!_positioned)
        {
            MoveTo(_position);
            _positioned = true;
        }
        ScheduleFrame();
    }

    internal void SetSelected(bool selected) { _selected = selected; UpdateBorder(); }

    internal void SetClickThrough(bool enabled)
    {
        _clickThrough = enabled;
        ApplyClickThrough();
        UpdateBorder();
    }

    internal void ReplaceImage(StickerContent content)
    {
        _content = content;
        _frameIndex = 0;
        _grayscale = _invert = false;
        RefreshFrame();
        ApplyState();
        ScheduleFrame();
        _manager.Changed();
    }

    private static StickerTransformState Normalize(StickerTransformState state) => state with
    {
        Scale = double.IsFinite(state.Scale) ? Math.Clamp(state.Scale, 0.1, 8) : 1,
        Opacity = double.IsFinite(state.Opacity) ? Math.Clamp(state.Opacity, 0.1, 1) : 1,
        RotationDegrees = ((state.RotationDegrees % 360 + 360) % 360 / 90) * 90
    };

    private PixelPoint GetPosition()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var inset = GlowInset();
        return handle != 0 && NativeMethods.GetWindowRect(handle, out var rect)
            ? new PixelPoint(rect.Left + inset.X, rect.Top + inset.Y) : _position;
    }

    private PixelPoint GlowInset()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        return new PixelPoint((int)Math.Round(8 * dpi.DpiScaleX), (int)Math.Round(8 * dpi.DpiScaleY));
    }

    private void MoveTo(PixelPoint location)
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(location.X, location.Y));
        var work = screen.WorkingArea;
        var x = Math.Clamp(location.X, work.Left, Math.Max(work.Left, work.Right - 40));
        var y = Math.Clamp(location.Y, work.Top, Math.Max(work.Top, work.Bottom - 40));
        var handle = new WindowInteropHelper(this).Handle;
        var inset = GlowInset();
        if (handle != 0 && NativeMethods.SetWindowPos(handle, 0, x - inset.X, y - inset.Y, 0, 0,
                NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate))
            _position = GetPosition();
    }

    private void OnStickerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _manager.Select(this); return; }
        if (e.ClickCount == 2)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Change(window => window._thumbnail = !window._thumbnail);
            else _manager.Hide(this);
            return;
        }
        Activate();
        if (CaptureMouse()) _dragStart = e.GetPosition(this);
    }

    private void CancelPendingDrag()
    {
        if (!_dragStart.HasValue) return;
        _dragStart = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnStickerMouseMove(object sender, MouseEventArgs e)
    {
        UpdateColorSample(e.GetPosition(StickerImage));
        if (_dragStart is not { } start) return;
        if (_clickThrough || e.LeftButton != MouseButtonState.Pressed)
        {
            CancelPendingDrag();
            return;
        }
        var offset = e.GetPosition(this) - start;
        if (Math.Abs(offset.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(offset.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        CancelPendingDrag();
        var original = GetPosition();
        DragMove();
        var moved = GetPosition();
        var dx = moved.X - original.X;
        var dy = moved.Y - original.Y;
        if (dx == 0 && dy == 0) return;
        foreach (var target in _manager.Targets(this).Where(window => !ReferenceEquals(window, this)))
        {
            var position = target.GetPosition();
            target.MoveTo(new PixelPoint(position.X + dx, position.Y + dy));
        }
    }

    private void OnStickerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        Adjust(e.Delta > 0);
        e.Handled = true;
    }

    private void OnStickerRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_cropStart is not { } start) return;
        var end = e.GetPosition(Frame);
        _cropStart = null;
        if (Mouse.Captured == Frame) Mouse.Capture(null);
        if (Math.Max(Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)) < 10) return;
        e.Handled = true;
        RunOutput(() =>
        {
            var displayed = StickerImage.TransformToAncestor(Frame)
                .TransformBounds(new Rect(0, 0, StickerImage.ActualWidth, StickerImage.ActualHeight));
            var selected = Rect.Intersect(displayed, new Rect(start, end));
            if (selected.IsEmpty || selected.Width < 1 || selected.Height < 1) return;
            var source = new TransformedBitmap(_displayed, TransformImage());
            var sx = source.PixelWidth / displayed.Width;
            var sy = source.PixelHeight / displayed.Height;
            var left = Math.Clamp((int)Math.Floor((selected.Left - displayed.Left) * sx), 0, source.PixelWidth - 1);
            var top = Math.Clamp((int)Math.Floor((selected.Top - displayed.Top) * sy), 0, source.PixelHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling((selected.Right - displayed.Left) * sx), left + 1, source.PixelWidth);
            var bottom = Math.Clamp((int)Math.Ceiling((selected.Bottom - displayed.Top) * sy), top + 1, source.PixelHeight);
            _manager.Create(new CroppedBitmap(source, new Int32Rect(left, top, right - left, bottom - top)), GetPosition());
        });
    }

    private void Adjust(bool increase)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Change(window => window._state = window._state.ChangeOpacity(increase ? 0.05 : -0.05));
        else if (_content.Frames.Length > 1)
        {
            _speed = Math.Clamp(_speed * (increase ? 1.25 : 0.8), 0.1, 8);
            ScheduleFrame();
        }
        else Change(window => window._state = window._state.Zoom(increase ? 1.1 : 1 / 1.1));
    }

    private void Change(Action<StickerWindow> action)
    {
        foreach (var window in _manager.Targets(this))
        {
            action(window);
            window.ApplyState();
        }
        _manager.Changed();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.P: ToggleClickThrough(); break;
            case Key.Escape:
                var action = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
                    ? _manager.Preferences.StickerShiftEscape : _manager.Preferences.StickerEscape;
                if (action == StickerEscapeAction.Hide) _manager.Hide(this);
                else if (action == StickerEscapeAction.Close) CloseSelected();
                else if (action == StickerEscapeAction.DisableClickThrough) _manager.DisableAllClickThrough();
                break;
            case Key.Delete: CloseSelected(); break;
            case Key.D1 when _content.Frames.Length > 1: StepAnimation(-1); break;
            case Key.D2 when _content.Frames.Length > 1: StepAnimation(1); break;
            case Key.D3: Flip(true); break;
            case Key.D4: Flip(false); break;
            case Key.D5: Filter(true); break;
            case Key.D6: Filter(false); break;
            case Key.Add or Key.OemPlus: Adjust(true); break;
            case Key.Subtract or Key.OemMinus: Adjust(false); break;
            case Key.Space when _content.Frames.Length > 1: ToggleAnimation(); break;
            case Key.Space: _manager.Edit?.Invoke(this); break;
            case Key.System when e.SystemKey == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt):
                RunOutput(CopySample); break;
            case Key.C when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): CopyImage(); break;
            case Key.S when Keyboard.Modifiers.HasFlag(ModifierKeys.Control): Save(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ColorSample.Visibility = Visibility.Collapsed;
    }

    private void StepAnimation(int direction)
    {
        _playing = false;
        AdvanceFrame(direction);
    }

    private void Rotate(int direction) =>
        Change(window => window._state = window._state with
            { RotationDegrees = (window._state.RotationDegrees + 360 + direction * 90) % 360 });

    private void Flip(bool horizontal) => Change(window => window._state = horizontal
        ? window._state with { FlipHorizontal = !window._state.FlipHorizontal }
        : window._state with { FlipVertical = !window._state.FlipVertical });

    private void Filter(bool grayscale) => Change(window =>
    {
        if (grayscale) window._grayscale = !window._grayscale;
        else window._invert = !window._invert;
        window.RefreshFrame();
    });

    private void ToggleAnimation() { _playing = !_playing; ScheduleFrame(); }

    private void AdvanceFrame(int direction)
    {
        _frameIndex = (_frameIndex + direction + _content.Frames.Length) % _content.Frames.Length;
        RefreshFrame();
        ScheduleFrame();
    }

    private void ScheduleFrame()
    {
        _animation.Stop();
        if (!_playing || _content.Frames.Length < 2 || !IsVisible) return;
        _animation.Interval = TimeSpan.FromMilliseconds(Math.Max(16, _content.Delays[_frameIndex] / _speed));
        _animation.Start();
    }

    private void RefreshFrame()
    {
        _displayed = _content.Frames[_frameIndex];
        if (_grayscale || _invert)
        {
            var source = new FormatConvertedBitmap(_displayed, PixelFormats.Bgra32, null, 0);
            var stride = checked(source.PixelWidth * 4);
            var bytes = new byte[checked(stride * source.PixelHeight)];
            source.CopyPixels(bytes, stride, 0);
            for (var i = 0; i < bytes.Length; i += 4)
            {
                if (_grayscale)
                    bytes[i] = bytes[i + 1] = bytes[i + 2] = (byte)((29 * bytes[i] + 150 * bytes[i + 1] + 77 * bytes[i + 2] + 128) >> 8);
                if (_invert)
                    for (var channel = 0; channel < 3; channel++) bytes[i + channel] = (byte)(255 - bytes[i + channel]);
            }
            _displayed = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, bytes, stride);
            _displayed.Freeze();
        }
        StickerImage.Source = _displayed;
        SetImageSize(VisualTreeHelper.GetDpi(this));
    }

    private void ApplyState()
    {
        Opacity = _state.Opacity;
        SetImageSize(VisualTreeHelper.GetDpi(this));
        StickerImage.LayoutTransform = TransformImage();
        Frame.Background = _background switch
        {
            StickerBackground.Clickable => new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            StickerBackground.SolidWhite => Brushes.White,
            StickerBackground.LightCheckerboard => Checker(Colors.White, Colors.LightGray),
            StickerBackground.DarkCheckerboard => Checker(Color.FromRgb(39, 39, 39), Color.FromRgb(68, 68, 68)),
            _ => Brushes.Transparent
        };
        UpdateBorder();
    }

    private void SetImageSize(DpiScale dpi)
    {
        var width = _displayed.PixelWidth;
        var height = _displayed.PixelHeight;
        var rotated = _state.RotationDegrees is 90 or 270;
        var scale = _thumbnail ? Math.Min(1, _thumbnailWidth / (rotated ? height : width)) : _state.Scale;
        // A pinned capture at 100% occupies its original physical pixels on each monitor.
        StickerImage.Width = Math.Max(1, Math.Round(width * scale)) / (rotated ? dpi.DpiScaleY : dpi.DpiScaleX);
        StickerImage.Height = Math.Max(1, Math.Round(height * scale)) / (rotated ? dpi.DpiScaleX : dpi.DpiScaleY);
        StickerSurface.Width = Math.Max(Frame.MinWidth, rotated ? StickerImage.Height : StickerImage.Width);
        StickerSurface.Height = Math.Max(Frame.MinHeight, rotated ? StickerImage.Width : StickerImage.Height);
        QueueNativeSizeUpdate();
    }

    private void QueueNativeSizeUpdate()
    {
        if (_nativeSizeUpdatePending || WindowHandle == 0) return;
        _nativeSizeUpdatePending = true;
        Dispatcher.InvokeAsync(() =>
        {
            _nativeSizeUpdatePending = false;
            ResizeNativeWindow();
        }, DispatcherPriority.Loaded);
    }

    private void ResizeNativeWindow()
    {
        if (WindowHandle == 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        // WPF auto-sizing can cap a borderless window at one screen when the image spans screens.
        var margin = StickerSurface.Margin;
        var desiredWidth = (int)Math.Round((StickerSurface.Width + margin.Left + margin.Right) * dpi.DpiScaleX);
        var desiredHeight = (int)Math.Round((StickerSurface.Height + margin.Top + margin.Bottom) * dpi.DpiScaleY);
        if (NativeMethods.GetWindowRect(WindowHandle, out var rect) &&
            (rect.Right - rect.Left != desiredWidth || rect.Bottom - rect.Top != desiredHeight))
            NativeMethods.SetWindowPos(WindowHandle, 0, 0, 0, desiredWidth, desiredHeight,
                NativeMethods.SwpNoMove | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpNoSendChanging);
    }

    private TransformGroup TransformImage() => new()
    {
        Children = { new ScaleTransform(_state.FlipHorizontal ? -1 : 1, _state.FlipVertical ? -1 : 1), new RotateTransform(_state.RotationDegrees) }
    };

    private static DrawingBrush Checker(Color light, Color dark)
    {
        var cells = new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } };
        var drawing = new DrawingGroup { Children =
        {
            new GeometryDrawing(new SolidColorBrush(light), null, new RectangleGeometry(new Rect(0, 0, 16, 16))),
            new GeometryDrawing(new SolidColorBrush(dark), null, cells)
        } };
        return new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16) };
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var current = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GwlExStyle).ToInt64();
        var next = _clickThrough ? current | NativeMethods.WsExTransparent : current & ~NativeMethods.WsExTransparent;
        NativeMethods.SetWindowLongPtr(handle, NativeMethods.GwlExStyle, new nint(next));
        NativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0,
            NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged | NativeMethods.SwpNoActivate);
    }

    private void UpdateBorder()
    {
        GlowOutline.Visibility = _clickThrough ? Visibility.Hidden : Visibility.Visible;
        GlowOutline.BorderBrush = new SolidColorBrush(_selected ? Color.FromRgb(46, 130, 250) : Color.FromArgb(173, 88, 155, 255));
        StickerGlow.Opacity = _selected ? 1 : 0.85;
    }

    private void ToggleClickThrough()
    {
        var next = !_clickThrough;
        Change(window => window.SetClickThrough(next));
    }

    private void UpdateColorSample(Point point)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) ||
            point.X < 0 || point.Y < 0 || point.X >= StickerImage.Width || point.Y >= StickerImage.Height)
        {
            ColorSample.Visibility = Visibility.Collapsed;
            return;
        }
        var x = (int)(point.X * _displayed.PixelWidth / StickerImage.Width);
        var y = (int)(point.Y * _displayed.PixelHeight / StickerImage.Height);
        var bytes = new byte[4];
        var image = _displayed.Format == PixelFormats.Bgra32 ? _displayed : new FormatConvertedBitmap(_displayed, PixelFormats.Bgra32, null, 0);
        image.CopyPixels(new Int32Rect(x, y, 1, 1), bytes, 4, 0);
        ColorSample.Text = $"#{bytes[2]:X2}{bytes[1]:X2}{bytes[0]:X2}";
        ColorSample.Visibility = Visibility.Visible;
    }

    private void CopySample()
    {
        UpdateColorSample(Mouse.GetPosition(StickerImage));
        if (ColorSample.Visibility == Visibility.Visible) Clipboard.SetText(ColorSample.Text);
    }

    private void CloseSelected()
    {
        foreach (var window in _manager.Targets(this)) window.Close();
    }

    private void CopyImage() => RunOutput(() => new ClipboardService().SetPng(ClipboardService.EncodePng(RenderCurrentImage())));

    private void Save() => RunOutput(() =>
    {
        var gif = _content.OriginalGif;
        var dialog = new SaveFileDialog
        {
            Filter = gif is null ? "PNG 圖片|*.png|JPEG 圖片|*.jpg" : "PNG 圖片|*.png|JPEG 圖片|*.jpg|原始 GIF 動畫|*.gif",
            FileName = $"MySnip-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        if (dialog.ShowDialog(this) != true) return;
        var extension = Path.GetExtension(dialog.FileName);
        File.WriteAllBytes(dialog.FileName, gif is not null && extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? gif : BitmapFactory.Encode(RenderCurrentImage(), extension));
    });

    private void RunOutput(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        { MessageBox.Show(this, $"無法輸出貼圖：{ex.Message}", "MySnip"); }
    }

    private BitmapSource RenderCurrentImage()
    {
        var transform = TransformImage();
        if (Math.Abs(_state.Scale - 1) > 0.001) transform.Children.Add(new ScaleTransform(_state.Scale, _state.Scale));
        var transformed = new TransformedBitmap(_displayed, transform);
        var width = transformed.PixelWidth;
        var height = transformed.PixelHeight;
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

    private void PopulateContextMenu()
    {
        ContextMenu.Items.Clear();
        Add(ContextMenu, "複製", CopyImage, "Ctrl+C");
        Add(ContextMenu, "使用 Windows 分享…", () => RunOutput(() =>
        {
            _share ??= new WindowsShare();
            _share.Show(WindowHandle, ClipboardService.EncodePng(RenderCurrentImage()));
        }), enabled: OperatingSystem.IsWindowsVersionAtLeast(10));
        Add(ContextMenu, "另存圖片…", Save);
        Add(ContextMenu, "從圖片網址載入…", () =>
        {
            var value = InputDialog.Show(this, "圖片網址", "輸入 HTTP 或 HTTPS 圖片網址", "", text => WebStickerService.TryParse(text, out _));
            if (value is not null && WebStickerService.TryParse(value, out var uri)) _manager.OpenWebImage?.Invoke(this, uri);
        });
        Add(ContextMenu, "編輯標註…", () => _manager.Edit?.Invoke(this), "Space", _content.Frames.Length == 1);
        Add(ContextMenu, "裁切圖片…", () =>
        {
            if (StickerCropWindow.ShowCrop(this, _content.Frames[0]) is { } cropped)
                ReplaceImage(new StickerContent(cropped));
        }, enabled: _content.Frames.Length == 1);
        if (_content.Color is { } color)
            Add(ContextMenu, "編輯色卡…", () =>
            {
                var input = InputDialog.Show(this, "色卡", "輸入 HEX 或 RGB 色碼", color, text => StickerText.TryParseColor(text, out _));
                if (input is not null && StickerText.TryParseColor(input, out var parsed)) ReplaceImage(StickerText.ColorCard(parsed));
            });
        ContextMenu.Items.Add(new Separator());
        if (_content.Frames.Length > 1)
        {
            Add(ContextMenu, "上一格", () => StepAnimation(-1), "1");
            Add(ContextMenu, "下一格", () => StepAnimation(1), "2");
        }
        else
        {
            Add(ContextMenu, "逆時針旋轉 90°", () => Rotate(-1));
            Add(ContextMenu, "順時針旋轉 90°", () => Rotate(1));
        }
        Add(ContextMenu, "水平翻轉", () => Flip(true), "3");
        Add(ContextMenu, "垂直翻轉", () => Flip(false), "4");
        Add(ContextMenu, "灰階", () => Filter(true), "5", check: _grayscale);
        Add(ContextMenu, "反相", () => Filter(false), "6", check: _invert);
        if (_content.Frames.Length > 1) Add(ContextMenu, "播放 GIF", ToggleAnimation, "Space", check: _playing);
        Add(ContextMenu, "設定縮放比例…", InputZoom);
        Add(ContextMenu, "重設大小與角度", () => Change(window => { window._state = new(); window._thumbnail = false; }));
        Add(ContextMenu, "縮圖模式", () => Change(window => window._thumbnail = !window._thumbnail), "Shift+雙擊", check: _thumbnail);
        Add(ContextMenu, "設定縮圖寬度…", InputThumbnail);
        ContextMenu.Items.Add(new Separator());
        var backgrounds = new MenuItem { Header = "透明區域背景" };
        ContextMenu.Items.Add(backgrounds);
        AddBackground("透明", StickerBackground.Transparent);
        AddBackground("透明且可點擊", StickerBackground.Clickable);
        AddBackground("白底可點擊", StickerBackground.SolidWhite);
        AddBackground("淺色棋盤格", StickerBackground.LightCheckerboard);
        AddBackground("深色棋盤格", StickerBackground.DarkCheckerboard);
        var groups = new MenuItem { Header = "移至群組" };
        ContextMenu.Items.Add(groups);
        foreach (var group in _manager.Groups) Add(groups, group, () => _manager.MoveToGroup(this, group), check: Group == group);
        Add(ContextMenu, "永遠置頂", () => { var next = !Topmost; Change(window => window.Topmost = next); }, check: Topmost);
        Add(ContextMenu, "滑鼠穿透", ToggleClickThrough, "P", check: _clickThrough);
        Add(ContextMenu, "只顯示選取貼圖", () => _manager.ToggleSolo(this), check: _manager.Solo);
        Add(ContextMenu, "隱藏", () => _manager.Hide(this), "雙擊");
        Add(ContextMenu, "關閉", CloseSelected, "Delete");

        void AddBackground(string label, StickerBackground background) =>
            Add(backgrounds, label, () => Change(window => window._background = background), check: _background == background);
    }

    private void InputZoom()
    {
        var value = InputDialog.Show(this, "貼圖縮放", "輸入縮放百分比（10–800）",
            (_state.Scale * 100).ToString("0", CultureInfo.InvariantCulture),
            text => int.TryParse(text, out var zoom) && zoom is >= 10 and <= 800);
        if (value is not null)
            Change(window => { window._state = window._state with { Scale = int.Parse(value, CultureInfo.InvariantCulture) / 100d }; window._thumbnail = false; });
    }

    private void InputThumbnail()
    {
        var value = InputDialog.Show(this, "縮圖寬度", "輸入寬度（40–1000 DIP）",
            _thumbnailWidth.ToString("0", CultureInfo.InvariantCulture),
            text => int.TryParse(text, out var width) && width is >= 40 and <= 1000);
        if (value is not null)
            Change(window => { window._thumbnailWidth = int.Parse(value, CultureInfo.InvariantCulture); window._thumbnail = true; });
    }

    private static void Add(ItemsControl parent, string label, Action action, string shortcut = "", bool enabled = true, bool? check = null)
    {
        var item = new MenuItem { Header = label, InputGestureText = shortcut, IsEnabled = enabled, IsCheckable = check.HasValue, IsChecked = check == true };
        item.Click += (_, _) => action();
        parent.Items.Add(item);
    }
}
