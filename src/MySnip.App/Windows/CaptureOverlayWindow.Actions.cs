using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MySnip.App.Interop;
using MySnip.App.Services;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private enum CaptureBoard { Desktop, White, Transparent }

    private CaptureBoard _board;
    private int _historyIndex = -1;
    private double _ratio;
    private bool _rounded;
    private bool _border;
    private bool _shadow;
    private bool _useDip;
    private bool _refreshing;
    private bool _stickOnCompletion;
    private bool _editingSticker;
    private bool _recognizing;
    private WindowsShare? _share;
    private PixelRect _stickerEditSelection;

    internal IReadOnlyList<CaptureRecord> History { get; init; } = [];
    internal Func<CaptureRecord, BitmapSource>? LoadHistoryImage { get; init; }
    internal Func<CapturedFrame>? RefreshFrame { get; init; }
    internal Action<string>? Notify { get; init; }
    internal bool CommandOutput { get; init; }
    internal bool AutoCommandOutput { get; init; }
    internal bool OutputShadow { set => _shadow = value; }
    internal PixelRect? ActiveWindowRegion { get; init; }
    internal (PixelRect Region, BitmapSource Image, bool Pinned, bool Saved)? CompletedCapture { get; private set; }

    internal void SelectGlobalRegion(PixelRect region) => CommitSelection(ToLocal(region));

    internal void EditSticker(BitmapSource source, PixelRect region)
    {
        _editingSticker = true;
        SelectGlobalRegion(region);
        _stickerEditSelection = _selection;
        ShowReplayImage(source);
        PinButton.ToolTip = "套用標註 (Enter)";
    }

    private void CompleteCapture(Action<BitmapSource> output, bool saved = false)
    {
        if (!_isSelectionCommitted || _selection.IsEmpty || _refreshing) return;
        try
        {
            var image = CaptureOutput.Decorate(RenderSelection(), _rounded, _border, _shadow);
            output(image);
            if (!_editingSticker)
                CompletedCapture = (GlobalSelection(), image, _stickOnCompletion, saved);
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.ComponentModel.Win32Exception or System.Printing.PrintSystemException)
        {
            MessageBox.Show(this, $"無法輸出截圖：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _stickOnCompletion = false; }
    }

    private void PinAndClose()
    {
        if (_editingSticker) { CompleteCapture(_createSticker); return; }
        if (!_isSelectionCommitted || _refreshing) return;
        _stickOnCompletion = true;
        CompleteCapture(_ => { });
    }

    private void QuickSave() => CompleteCapture(image =>
    {
        var path = CaptureOutput.QuickSave(image, _outputDirectory);
        Notify?.Invoke($"已儲存 {path}");
    }, saved: true);

    private PixelRect GlobalSelection() => new(_selection.X + _frame.Bounds.X, _selection.Y + _frame.Bounds.Y,
        _selection.Width, _selection.Height);

    private void Input_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            PinAndClose();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            if (_lineStrip is not null) FinishLineStrip();
            else if (_tool == AnnotationTool.Select && FindAnnotationAt(PointerPosition(e)) is { } element) ShowAnnotationOptions(element);
            else ShowOptions(InputCanvas, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
            e.Handled = true;
        }
    }

    private void Options_Click(object sender, RoutedEventArgs e) =>
        ShowOptions((UIElement)sender, System.Windows.Controls.Primitives.PlacementMode.Bottom);

    private void ShowOptions(UIElement target, System.Windows.Controls.Primitives.PlacementMode placement)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        Add("選取目前螢幕", SelectCurrentScreen, "F");
        Add("選取所有螢幕", () => CommitSelection(CanvasBounds), "Shift+F");
        Add("選取啟動截圖前的視窗", () => { if (ActiveWindowRegion is { } active) SelectGlobalRegion(active); }, enabled: ActiveWindowRegion.HasValue);
        Add("依大小選取…", InputRegionSize);
        Add("固定比例…", InputRatio);
        Add("切換 UI 元素範圍", CycleCandidate, "Tab", !_editingSticker);
        Add("重新擷取桌面", () => _ = RefreshAsync(), "R", RefreshFrame is not null);
        menu.Items.Add(new Separator());
        Add("桌面", () => SetBoard(CaptureBoard.Desktop), enabled: !_editingSticker, check: _board == CaptureBoard.Desktop);
        Add("白板", () => SetBoard(CaptureBoard.White), enabled: !_editingSticker, check: _board == CaptureBoard.White);
        Add("透明白板", () => SetBoard(CaptureBoard.Transparent), enabled: !_editingSticker, check: _board == CaptureBoard.Transparent);
        Add("以 DIP 顯示尺寸", () => { _useDip = !_useDip; UpdateSelectionVisual(); }, check: _useDip);
        Add("編號標註", () => SetTool(AnnotationTool.Number), "N", _isSelectionCommitted, _tool == AnnotationTool.Number);
        Add("自由選取標註", () => { _freeSelect = !_freeSelect; if (_freeSelect) SetTool(AnnotationTool.Select); },
            "Shift+拖曳", _isSelectionCommitted, _freeSelect);
        Add("圓形橡皮擦", () => { _roundEraser = !_roundEraser; SetTool(AnnotationTool.Eraser); },
            enabled: _isSelectionCommitted, check: _roundEraser);
        menu.Items.Add(new Separator());
        Add("輸出圓角", () => _rounded = !_rounded, check: _rounded);
        Add("輸出外框", () => _border = !_border, check: _border);
        Add("輸出陰影", () => _shadow = !_shadow, check: _shadow);
        Add("快速儲存 PNG", QuickSave, "Ctrl+Shift+S", _isSelectionCommitted);
        Add("複製圖片與 PNG 檔案", CopyImageAsFile, enabled: _isSelectionCommitted);
        Add("擷取文字（OCR）…", () => _ = RecognizeAsync(false), enabled: _isSelectionCommitted && !_recognizing);
        Add("讀取 QR Code／條碼…", () => _ = RecognizeAsync(true), enabled: _isSelectionCommitted && !_recognizing);
        Add("列印…", PrintSelection, enabled: _isSelectionCommitted);
        Add("使用 Windows 分享…", ShareSelection, enabled: _isSelectionCommitted && WindowsShare.IsSupported());
        Add("在其他應用程式開啟", OpenSelection, enabled: _isSelectionCommitted);
        Add("上一筆截圖歷史", () => ReplayHistory(1), ",", History.Count > 0);
        Add("下一筆截圖歷史", () => ReplayHistory(-1), ".", History.Count > 0);
        Add("複製游標色碼", CopyColor, "C");
        menu.IsOpen = true;

        void Add(string title, Action action, string shortcut = "", bool enabled = true, bool? check = null)
        {
            var item = new MenuItem
            {
                Header = title, InputGestureText = shortcut, IsEnabled = enabled,
                IsCheckable = check.HasValue, IsChecked = check == true
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private bool HandleCaptureShortcut(KeyEventArgs e)
    {
        if (HandleAnnotationShortcut(e)) return true;
        var modifiers = Keyboard.Modifiers;
        var control = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (e.Key == Key.F && !control)
        {
            if (shift) CommitSelection(CanvasBounds); else SelectCurrentScreen();
        }
        else if (e.Key == Key.C && !control) CopyColor();
        else if (e.Key == Key.N && modifiers == ModifierKeys.None && _isSelectionCommitted) SetTool(AnnotationTool.Number);
        else if (e.Key == Key.S && control && shift) QuickSave();
        else if (e.Key == Key.OemComma) ReplayHistory(1);
        else if (e.Key == Key.OemPeriod) ReplayHistory(-1);
        else if (e.Key == Key.R && modifiers == ModifierKeys.None) _ = RefreshAsync();
        else if (e.Key == Key.Tab && !_isSelectionCommitted) CycleCandidate();
        else if (!_isSelectionCommitted && (e.Key is Key.Left or Key.Right or Key.Up or Key.Down ||
            e.Key is Key.A or Key.S or Key.D && modifiers == ModifierKeys.None))
        {
            var point = ToGlobal(ToPixel(_currentPoint));
            NativeMethods.SetCursorPos(point.X + (e.Key is Key.Left or Key.A ? -1 : e.Key is Key.Right or Key.D ? 1 : 0),
                point.Y + (e.Key is Key.Up ? -1 : e.Key is Key.Down or Key.S ? 1 : 0));
        }
        else if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) UpdateMagnifier(_currentPoint);
        else return false;
        return true;
    }

    private void CopyColor()
    {
        try
        {
            var color = CapturedPixelSampler.Sample(_frame, ToGlobal(ToPixel(_currentPoint)));
            Clipboard.SetText(_showHex ? color.ToHexText() : color.ToRgbText());
            _recordedCanceled = true;
            DialogResult = true;
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        { MessageBox.Show(this, $"無法複製色碼：{ex.Message}", "MySnip"); }
    }

    private async Task RecognizeAsync(bool codes)
    {
        if (!_isSelectionCommitted || _recognizing) return;
        _recognizing = true;
        try
        {
            var image = RenderSelection();
            var text = codes ? await ImageRecognitionService.DecodeCodesAsync(image) : await ImageRecognitionService.ExtractTextAsync(image);
            if (!IsVisible) return;
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, codes ? "選取區域中沒有辨識到條碼。" : "選取區域中沒有辨識到文字。", "MySnip");
                return;
            }
            var result = InputDialog.Show(this, codes ? "條碼結果" : "文字辨識結果", "按確定複製辨識結果。", text,
                value => value.Length > 0, multiline: true);
            if (result is not null) Clipboard.SetText(result);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
            or TimeoutException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        { if (IsVisible) MessageBox.Show(this, $"無法辨識影像：{ex.Message}", "MySnip"); }
        finally { _recognizing = false; }
    }

    private void PrintSelection()
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        CompleteCapture(image => CaptureOutput.Print(image, dialog));
    }

    private void OpenSelection() => CompleteCapture(image =>
    {
        var path = CaptureOutput.QuickSave(image, _outputDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }, saved: true);

    private void ShareSelection()
    {
        if (!_isSelectionCommitted) return;
        try
        {
            var image = CaptureOutput.Decorate(RenderSelection(), _rounded, _border, _shadow);
            _share ??= new WindowsShare();
            _share.Show(new WindowInteropHelper(this).Handle, ClipboardService.EncodePng(image));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { MessageBox.Show(this, $"無法開啟 Windows 分享：{ex.Message}", "MySnip"); }
    }

    private void CopyImageAsFile() => CompleteCapture(image =>
    {
        var path = CaptureOutput.QuickSave(image, _outputDirectory);
        var clipboard = _clipboard as ClipboardService ?? new ClipboardService();
        clipboard.SetPng(ClipboardService.EncodePng(image), path);
    }, saved: true);

    private void SetBoard(CaptureBoard mode)
    {
        _board = mode;
        Screenshot.Visibility = mode == CaptureBoard.Desktop ? Visibility.Visible : Visibility.Collapsed;
        ReplayedScreenshot.Visibility = mode == CaptureBoard.Desktop && ReplayedScreenshot.Source is not null
            ? Visibility.Visible : Visibility.Collapsed;
        Background = mode == CaptureBoard.White ? Brushes.White : mode == CaptureBoard.Transparent ? Brushes.LightGray : Brushes.Black;
    }

    private void ReplayHistory(int direction)
    {
        if (History.Count == 0 || LoadHistoryImage is null) return;
        var index = Math.Clamp(_historyIndex + direction, 0, History.Count - 1);
        try
        {
            var entry = History[index];
            var image = LoadHistoryImage(entry);
            _historyIndex = index;
            ClearAnnotations();
            SetBoard(CaptureBoard.Desktop);
            var region = ToLocal(entry.Region);
            var placement = new PixelRect(region.X, region.Y,
                Math.Min(image.PixelWidth, CanvasBounds.Width), Math.Min(image.PixelHeight, CanvasBounds.Height));
            CommitSelection(placement.MoveBy(0, 0, CanvasBounds));
            ShowReplayImage(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { MessageBox.Show(this, $"無法開啟截圖歷史：{ex.Message}", "MySnip"); }
    }

    private void ShowReplayImage(BitmapSource source)
    {
        ReplayedScreenshot.Source = source;
        ReplayedScreenshot.Visibility = Visibility.Visible;
        ReplayedScreenshot.Width = _selection.Width;
        ReplayedScreenshot.Height = _selection.Height;
        ReplayedScreenshot.Stretch = Stretch.Fill;
        Canvas.SetLeft(ReplayedScreenshot, _selection.X);
        Canvas.SetTop(ReplayedScreenshot, _selection.Y);
    }

    private void ClearAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        _annotations.Clear();
        _lineStrip = null;
        SelectAnnotations([]);
        _undo.Clear();
        UpdateHistoryButtons();
    }

    private void Input_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_isSelectionCommitted || _editingSticker) return;
        CycleCandidate(Math.Sign(e.Delta), wrap: false);
        e.Handled = true;
    }

    private void CycleCandidate() => CycleCandidate(1, wrap: true);

    private void CycleCandidate(int direction, bool wrap)
    {
        if (_editingSticker) return;
        var point = ToGlobal(ToPixel(_currentPoint));
        var window = CaptureCandidatePicker.PickBest(_candidates, point, _frame.Bounds, CaptureCandidateKind.TopLevelWindow);
        if (window is null) return;
        var current = _hoverCandidate ?? _selection;
        var stack = _candidates.Where(candidate => candidate.Layer == window.Value.Layer && candidate.Bounds.Contains(point))
            .Select(candidate => ToLocal(candidate.Bounds)).Distinct().OrderBy(bounds => (long)bounds.Width * bounds.Height).ToArray();
        if (!wrap && !current.IsEmpty)
            stack = stack.Where(bounds => bounds == current || direction > 0 && bounds.ClampTo(current) == current ||
                direction < 0 && current.ClampTo(bounds) == bounds).ToArray();
        if (stack.Length == 0) return;
        var index = Array.IndexOf(stack, current);
        _hoverCandidate = stack[wrap ? (index + direction + stack.Length) % stack.Length :
            Math.Clamp(index + direction, 0, stack.Length - 1)];
        _isSelectionCommitted = false;
        UpdateSelectionVisual();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing || RefreshFrame is null) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0) return;
        _refreshing = true;
        const uint positionFlags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate;
        try
        {
            if (!NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, positionFlags | NativeMethods.SwpHideWindow))
                throw new InvalidOperationException("無法暫時隱藏截圖視窗。");
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            NativeMethods.DwmFlush();
            if (!IsVisible) return;
            var frame = RefreshFrame();
            if (frame.Bounds != _frame.Bounds)
                throw new InvalidOperationException("螢幕配置已變更，請重新開始截圖。");
            _bitmap = BitmapFactory.FromFrame(frame);
            _frame = frame;
            Screenshot.Source = _bitmap;
            ReplayedScreenshot.Source = null;
            SetBoard(CaptureBoard.Desktop);
            UpdateMagnifier(_currentPoint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            if (IsVisible)
            {
                NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, positionFlags | NativeMethods.SwpShowWindow);
                MessageBox.Show(this, $"無法重新擷取桌面：{ex.Message}", "MySnip");
            }
        }
        finally
        {
            if (IsVisible)
            {
                NativeMethods.SetWindowPos(hwnd, 0, 0, 0, 0, 0, positionFlags | NativeMethods.SwpShowWindow);
                Activate();
            }
            _refreshing = false;
        }
    }

    private (double X, double Y) DisplayScale()
    {
        var point = ToGlobal(ToPixel(_currentPoint));
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = point.X, Y = point.Y }, NativeMethods.MonitorDefaultToNearest);
        return NativeMethods.GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 ? (x / 96d, y / 96d) : (1, 1);
    }

    private void InputRegionSize()
    {
        var scale = _useDip ? DisplayScale() : (X: 1d, Y: 1d);
        var size = InputDialog.Show(this, "截圖尺寸", $"寬 × 高（{(_useDip ? "DIP" : "px")}，例如 800 × 600）",
            _selection.IsEmpty ? "800 × 600" : $"{_selection.Width / scale.X:0} × {_selection.Height / scale.Y:0}",
            value => TryPair(value, out var w, out var h) && w * scale.X <= CanvasBounds.Width && h * scale.Y <= CanvasBounds.Height);
        if (size is null || !TryPair(size, out var width, out var height)) return;
        var position = _selection.IsEmpty ? ToPixel(_currentPoint) : new PixelPoint(_selection.X, _selection.Y);
        var region = new PixelRect(position.X, position.Y, (int)Math.Round(width * scale.X), (int)Math.Round(height * scale.Y));
        CommitSelection(region.MoveBy(0, 0, CanvasBounds));
    }

    private void InputRatio()
    {
        var value = InputDialog.Show(this, "截圖比例", "寬 : 高（例如 16:9）。輸入 0 取消固定比例。", _ratio == 0 ? "16:9" : $"{_ratio:0.###}:1",
            text => text == "0" || TryPair(text, out var w, out var h) && w / h is >= 0.02 and <= 50);
        if (value == "0") _ratio = 0;
        else if (value is not null && TryPair(value, out var width, out var height)) _ratio = width / height;
    }

    private static bool TryPair(string value, out double width, out double height)
    {
        width = height = 0;
        var parts = value.Split(['x', 'X', '×', ':'], StringSplitOptions.TrimEntries);
        return parts.Length == 2 && double.TryParse(parts[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out width) &&
            double.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out height) &&
            double.IsFinite(width) && double.IsFinite(height) && width >= 1 && height >= 1;
    }

    private PixelRect DragSelection(Point end)
    {
        var start = ToPixel(_start);
        var stop = ToPixel(end);
        if (_ratio > 0)
        {
            var dx = stop.X - start.X;
            var dy = stop.Y - start.Y;
            var width = Math.Min(Math.Abs(dx), Math.Abs(dy) * _ratio);
            stop = new PixelPoint(start.X + Math.Sign(dx) * (int)Math.Round(width),
                start.Y + Math.Sign(dy) * (int)Math.Round(width / _ratio));
        }
        return PixelRect.FromPoints(start, stop).ClampTo(CanvasBounds);
    }
}
