using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using MySnip.App.Services;
using MySnip.App.Interop;
using MySnip.Core.Commands;
using MySnip.Core.Models;
using MySnip.Core.Services;
using Cursor = System.Windows.Input.Cursor;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow : Window
{
    private enum DragMode { None, CreateSelection, MoveSelection, ResizeLeft, ResizeTop, ResizeRight, ResizeBottom, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight, Draw, MoveElement }

    private readonly CapturedFrame _frame;
    private readonly BitmapSource _bitmap;
    private readonly HashSet<CaptureCandidate> _candidates;
    private readonly IClipboardService _clipboard;
    private readonly Action<BitmapSource> _createSticker;
    private readonly string _outputDirectory;
    private readonly UndoStack _undo = new();
    private readonly List<FrameworkElement> _annotations = [];
    private readonly List<Rectangle> _handles = [];
    private PixelRect _selection;
    private PixelRect? _hoverCandidate;
    private PixelRect? _mouseDownCandidate;
    private bool _isSelectionCommitted;
    private Point _currentPoint;
    private bool _showHex;
    private Point _start;
    private PixelRect _startSelection;
    private DragMode _dragMode;
    private AnnotationTool _tool = AnnotationTool.Select;
    private FrameworkElement? _activeElement;
    private Point _elementStart;
    private double _elementOriginalLeft;
    private double _elementOriginalTop;
    private Color _drawingColor;
    private double _drawingWidth;
    private double _drawingOpacity;
    private int _filterStrength = 10;
    private AnnotationTool _lastShapeTool = AnnotationTool.Rectangle;
    private AnnotationTool _lastLineTool = AnnotationTool.Arrow;
    private AnnotationTool _lastHighlighterTool = AnnotationTool.Highlighter;
    private AnnotationTool _lastFilterTool = AnnotationTool.Mosaic;
    private bool _updatingStyleControls;
    private bool _fontsLoaded;
    private HwndSource? _windowSource;
    private bool _nativeLayoutUpdatePending;

    public CaptureOverlayWindow(CapturedFrame frame, IReadOnlyList<CaptureCandidate> candidates, IClipboardService clipboard,
        Action<BitmapSource> createSticker, string outputDirectory, ToolDefaults defaults)
    {
        InitializeComponent();
        _frame = frame;
        _candidates = candidates.ToHashSet();
        _clipboard = clipboard;
        _createSticker = createSticker;
        _outputDirectory = outputDirectory;
        _drawingColor = (Color)ColorConverter.ConvertFromString(defaults.Color);
        _drawingWidth = defaults.StrokeWidth;
        _drawingOpacity = defaults.Opacity;
        _bitmap = BitmapFactory.FromFrame(frame);
        Screenshot.Source = _bitmap;
        ConfigureCanvas();
        ConfigureStyleControls(defaults);
        CreateSelectionHandles();
        HelpText.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        Loaded += (_, _) =>
        {
            Focus();
            var cursor = System.Windows.Forms.Control.MousePosition;
            _currentPoint = ClampPoint(new Point(cursor.X - _frame.Bounds.X, cursor.Y - _frame.Bounds.Y));
            UpdateHover(_currentPoint);
            UpdateMagnifier(_currentPoint);
        };
    }

    internal void AddCaptureCandidates(IEnumerable<CaptureCandidate> candidates)
    {
        var changed = false;
        foreach (var candidate in candidates)
            changed |= _candidates.Add(candidate);

        if (changed && IsVisible && !_isSelectionCommitted && _dragMode == DragMode.None)
            UpdateHover(_currentPoint);
    }

    private void ConfigureStyleControls(ToolDefaults defaults)
    {
        _updatingStyleControls = true;
        WidthSlider.Value = Math.Clamp(defaults.StrokeWidth, 1, 32);
        OpacitySlider.Value = Math.Clamp(defaults.Opacity, 0.1, 1);
        FilterStrengthSlider.Value = _filterStrength;
        FontFamilyPicker.Items.Add(new FontFamily("Microsoft JhengHei UI"));
        FontFamilyPicker.SelectedIndex = 0;
        FontSizePicker.SelectedIndex = 2;
        _updatingStyleControls = false;
        UpdateToolUi();
        UpdateStyleLabels();
        UpdateColorPreview();
        UpdateHistoryButtons();
    }

    private void CreateSelectionHandles()
    {
        for (var i = 0; i < 8; i++)
        {
            var handle = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = new SolidColorBrush(Color.FromRgb(39, 167, 231)), StrokeThickness = 1 };
            _handles.Add(handle); MaskCanvas.Children.Add(handle);
        }
    }

    private void ConfigureCanvas()
    {
        Root.Width = InputCanvas.Width = AnnotationCanvas.Width = MaskCanvas.Width = _frame.Bounds.Width;
        Root.Height = InputCanvas.Height = AnnotationCanvas.Height = MaskCanvas.Height = _frame.Bounds.Height;
        UpdateSelectionVisual();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        ApplyPhysicalOverlayBounds();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == NativeMethods.WmDpiChanged && !_nativeLayoutUpdatePending)
        {
            _nativeLayoutUpdatePending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _nativeLayoutUpdatePending = false;
                ApplyPhysicalOverlayBounds();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (message == NativeMethods.WmDisplayChange)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (IsVisible)
                {
                    DialogResult = false;
                    MessageBox.Show("顯示器配置已變更，請重新開始截圖。", "MySnip", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            });
        }

        return 0;
    }

    private void ApplyPhysicalOverlayBounds()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;

        var dpi = NativeMethods.GetDpiForWindow(handle);
        if (dpi == 0) dpi = 96;
        var layout = OverlayCoordinateMapper.Create(_frame.Bounds, dpi, dpi);

        // WPF uses DIPs while capture and selection coordinates use physical pixels.
        // The inverse layout scale cancels WPF's per-monitor DPI scale, so one canvas
        // unit is rendered as exactly one physical pixel and GetPosition returns the
        // same physical-like local coordinates used by PixelRect.
        Root.LayoutTransform = new ScaleTransform(layout.InverseLayoutScaleX, layout.InverseLayoutScaleY);
        Root.InvalidateMeasure();

        if (!NativeMethods.SetWindowPos(handle, NativeMethods.HwndTopmost,
                layout.PhysicalBounds.X, layout.PhysicalBounds.Y,
                layout.PhysicalBounds.Width, layout.PhysicalBounds.Height,
                NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged))
        {
            throw new InvalidOperationException("無法將截圖覆蓋視窗定位到虛擬桌面。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
    }

    private PixelRect ToLocal(PixelRect global) => new(global.X - _frame.Bounds.X, global.Y - _frame.Bounds.Y, global.Width, global.Height);
    private PixelRect CanvasBounds => new(0, 0, _frame.Bounds.Width, _frame.Bounds.Height);

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Focus();
        _start = ClampPoint(e.GetPosition(InputCanvas));
        _currentPoint = _start;
        _startSelection = _selection;
        InputCanvas.CaptureMouse();

        if (!_isSelectionCommitted)
        {
            _mouseDownCandidate = _hoverCandidate;
            _hoverCandidate = null;
            _dragMode = DragMode.CreateSelection;
            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            UpdateSelectionVisual();
            return;
        }

        if (_tool == AnnotationTool.Select)
        {
            var hit = FindAnnotationAt(_start);
            if (hit is not null)
            {
                _activeElement = hit;
                _elementStart = _start;
                _elementOriginalLeft = GetLeft(hit);
                _elementOriginalTop = GetTop(hit);
                _dragMode = DragMode.MoveElement;
                return;
            }

            _dragMode = HitSelectionEdge(_start);
            if (_dragMode != DragMode.CreateSelection) return;

            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            _isSelectionCommitted = false;
            Toolbar.Visibility = Visibility.Collapsed;
            UpdateMagnifier(_start);
            return;
        }

        if (_selection.IsEmpty || !_selection.Contains(ToPixel(_start)))
        {
            _dragMode = DragMode.CreateSelection;
            _isSelectionCommitted = false;
            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            Toolbar.Visibility = Visibility.Collapsed;
            UpdateMagnifier(_start);
            return;
        }

        if (_tool == AnnotationTool.Eraser)
        {
            EraseAt(_start);
            _dragMode = DragMode.None;
            InputCanvas.ReleaseMouseCapture();
            return;
        }

        if (_tool == AnnotationTool.Text)
        {
            BeginText(_start);
            _dragMode = DragMode.None;
            InputCanvas.ReleaseMouseCapture();
            return;
        }

        if (_tool != AnnotationTool.Select)
        {
            _dragMode = DragMode.Draw;
            _activeElement = CreateDrawingElement(_tool, _start);
            if (_activeElement is not null) AnnotationCanvas.Children.Add(_activeElement);
            return;
        }

        _dragMode = DragMode.None;
    }

    private void Input_MouseMove(object sender, MouseEventArgs e)
    {
        var point = ClampToSelectionIfDrawing(ClampPoint(e.GetPosition(InputCanvas)));
        _currentPoint = point;
        if (!_isSelectionCommitted) UpdateMagnifier(point);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (!_isSelectionCommitted)
            {
                UpdateHover(point);
                Cursor = Cursors.Cross;
                return;
            }
            Cursor = CursorFor(HitSelectionEdge(point));
            return;
        }

        switch (_dragMode)
        {
            case DragMode.CreateSelection:
                _selection = PixelRect.FromPoints(ToPixel(_start), ToPixel(point)).ClampTo(CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.MoveSelection:
                _selection = _startSelection.MoveBy((int)(point.X - _start.X), (int)(point.Y - _start.Y), CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.ResizeLeft:
                _selection = PixelRect.FromPoints(new PixelPoint((int)point.X, _startSelection.Top), new PixelPoint(_startSelection.Right, _startSelection.Bottom)).ClampTo(CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.ResizeRight:
                _selection = PixelRect.FromPoints(new PixelPoint(_startSelection.Left, _startSelection.Top), new PixelPoint((int)point.X, _startSelection.Bottom)).ClampTo(CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.ResizeTop:
                _selection = PixelRect.FromPoints(new PixelPoint(_startSelection.Left, (int)point.Y), new PixelPoint(_startSelection.Right, _startSelection.Bottom)).ClampTo(CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.ResizeBottom:
                _selection = PixelRect.FromPoints(new PixelPoint(_startSelection.Left, _startSelection.Top), new PixelPoint(_startSelection.Right, (int)point.Y)).ClampTo(CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.ResizeTopLeft:
            case DragMode.ResizeTopRight:
            case DragMode.ResizeBottomLeft:
            case DragMode.ResizeBottomRight:
                _selection = SelectionGeometry.Resize(_startSelection, ToResizeHandle(_dragMode), ToPixel(point), CanvasBounds);
                UpdateSelectionVisual();
                break;
            case DragMode.Draw:
                UpdateDrawingElement(_activeElement, _start, point);
                break;
            case DragMode.MoveElement when _activeElement is not null:
                Canvas.SetLeft(_activeElement, _elementOriginalLeft + point.X - _elementStart.X);
                Canvas.SetTop(_activeElement, _elementOriginalTop + point.Y - _elementStart.Y);
                break;
        }
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var point = ClampToSelectionIfDrawing(ClampPoint(e.GetPosition(InputCanvas)));
        _currentPoint = point;
        InputCanvas.ReleaseMouseCapture();
        if (_dragMode == DragMode.CreateSelection && !_isSelectionCommitted)
        {
            var dragged = PixelRect.FromPoints(ToPixel(_start), ToPixel(point)).ClampTo(CanvasBounds);
            _selection = dragged.Width >= 3 || dragged.Height >= 3
                ? dragged
                : _mouseDownCandidate ?? default;
            _isSelectionCommitted = !_selection.IsEmpty;
            _mouseDownCandidate = null;
            if (_isSelectionCommitted) Magnifier.Visibility = Visibility.Collapsed;
            else
            {
                UpdateHover(point);
                UpdateMagnifier(point);
            }
        }
        else if (_dragMode == DragMode.Draw && _activeElement is not null)
        {
            var element = _activeElement;
            if (_tool is AnnotationTool.Mosaic or AnnotationTool.Blur)
            {
                AnnotationCanvas.Children.Remove(element);
                element = CreateFilteredRegion(PixelRect.FromPoints(ToPixel(_start), ToPixel(point)), _tool == AnnotationTool.Blur);
                AnnotationCanvas.Children.Add(element);
            }
            AddAnnotationCommand(element);
        }
        else if (_dragMode == DragMode.MoveElement && _activeElement is not null)
        {
            var element = _activeElement;
            var finalLeft = GetLeft(element);
            var finalTop = GetTop(element);
            Canvas.SetLeft(element, _elementOriginalLeft);
            Canvas.SetTop(element, _elementOriginalTop);
            _undo.Execute(new DelegateCommand(
                () => { Canvas.SetLeft(element, finalLeft); Canvas.SetTop(element, finalTop); },
                () => { Canvas.SetLeft(element, _elementOriginalLeft); Canvas.SetTop(element, _elementOriginalTop); }));
        }

        _activeElement = null;
        _dragMode = DragMode.None;
        UpdateSelectionVisual();
    }

    private DragMode HitSelectionEdge(Point point)
    {
        return SelectionHitTesting.HitTest(_selection, ToPixel(point)) switch
        {
            SelectionHitTarget.ResizeLeft => DragMode.ResizeLeft,
            SelectionHitTarget.ResizeTop => DragMode.ResizeTop,
            SelectionHitTarget.ResizeRight => DragMode.ResizeRight,
            SelectionHitTarget.ResizeBottom => DragMode.ResizeBottom,
            SelectionHitTarget.ResizeTopLeft => DragMode.ResizeTopLeft,
            SelectionHitTarget.ResizeTopRight => DragMode.ResizeTopRight,
            SelectionHitTarget.ResizeBottomLeft => DragMode.ResizeBottomLeft,
            SelectionHitTarget.ResizeBottomRight => DragMode.ResizeBottomRight,
            SelectionHitTarget.Move => DragMode.MoveSelection,
            _ => DragMode.CreateSelection
        };
    }

    private static Cursor CursorFor(DragMode mode) => mode switch
    {
        DragMode.ResizeLeft or DragMode.ResizeRight => Cursors.SizeWE,
        DragMode.ResizeTop or DragMode.ResizeBottom => Cursors.SizeNS,
        DragMode.ResizeTopLeft or DragMode.ResizeBottomRight => Cursors.SizeNWSE,
        DragMode.ResizeTopRight or DragMode.ResizeBottomLeft => Cursors.SizeNESW,
        DragMode.MoveSelection or DragMode.MoveElement => Cursors.SizeAll,
        _ => Cursors.Cross
    };

    private void UpdateSelectionVisual()
    {
        var isManualPreview = _dragMode == DragMode.CreateSelection && !_isSelectionCommitted;
        var s = (_isSelectionCommitted || isManualPreview ? _selection : _hoverCandidate ?? default).ClampTo(CanvasBounds);
        SetRect(MaskTop, 0, 0, _frame.Bounds.Width, s.Top);
        SetRect(MaskBottom, 0, s.Bottom, _frame.Bounds.Width, _frame.Bounds.Height - s.Bottom);
        SetRect(MaskLeft, 0, s.Top, s.Left, s.Height);
        SetRect(MaskRight, s.Right, s.Top, _frame.Bounds.Width - s.Right, s.Height);
        SelectionBorder.Width = Math.Max(0, s.Width);
        SelectionBorder.Height = Math.Max(0, s.Height);
        SelectionBorder.Visibility = s.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(SelectionBorder, s.Left);
        Canvas.SetTop(SelectionBorder, s.Top);
        SizeLabel.Text = s.IsEmpty ? string.Empty : $"{s.Width} × {s.Height} px";
        SizeLabel.Visibility = s.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        Canvas.SetLeft(SizeLabel, s.Left);
        Canvas.SetTop(SizeLabel, Math.Max(0, s.Top - 26));
        var showCommittedControls = _isSelectionCommitted && !_selection.IsEmpty;
        UpdateHandlePositions(s, showCommittedControls);
        Toolbar.Visibility = showCommittedControls ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var toolbarLeft = Math.Clamp(s.Left, 0, Math.Max(0, _frame.Bounds.Width - Toolbar.DesiredSize.Width));
        var below = s.Bottom + 8;
        var toolbarTop = below + Toolbar.DesiredSize.Height <= _frame.Bounds.Height ? below : Math.Max(0, s.Top - Toolbar.DesiredSize.Height - 8);
        Canvas.SetLeft(Toolbar, toolbarLeft);
        Canvas.SetTop(Toolbar, toolbarTop);
    }

    private void UpdateHandlePositions(PixelRect s, bool visible)
    {
        var positions = new[]
        {
            new Point(s.Left, s.Top), new Point((s.Left + s.Right) / 2d, s.Top), new Point(s.Right, s.Top),
            new Point(s.Left, (s.Top + s.Bottom) / 2d), new Point(s.Right, (s.Top + s.Bottom) / 2d),
            new Point(s.Left, s.Bottom), new Point((s.Left + s.Right) / 2d, s.Bottom), new Point(s.Right, s.Bottom)
        };
        for (var i = 0; i < _handles.Count; i++)
        {
            _handles[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(_handles[i], positions[i].X - 4); Canvas.SetTop(_handles[i], positions[i].Y - 4);
        }
    }

    private static ResizeHandle ToResizeHandle(DragMode mode) => mode switch
    {
        DragMode.ResizeTopLeft => ResizeHandle.TopLeft, DragMode.ResizeTopRight => ResizeHandle.TopRight,
        DragMode.ResizeBottomLeft => ResizeHandle.BottomLeft, DragMode.ResizeBottomRight => ResizeHandle.BottomRight,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static void SetRect(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left); Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width); element.Height = Math.Max(0, height);
    }

    private FrameworkElement? CreateDrawingElement(AnnotationTool tool, Point point)
    {
        var color = _drawingColor;
        Shape shape = tool switch
        {
            AnnotationTool.Rectangle => new Rectangle(),
            AnnotationTool.FilledRectangle => new Rectangle(),
            AnnotationTool.Ellipse => new Ellipse(),
            AnnotationTool.FilledEllipse => new Ellipse(),
            AnnotationTool.Line or AnnotationTool.Highlighter => new Line(),
            AnnotationTool.Arrow or AnnotationTool.OutlineArrow or AnnotationTool.FilledArrow => new System.Windows.Shapes.Path(),
            AnnotationTool.Pen => new Polyline(),
            AnnotationTool.HighlighterRectangle => new Rectangle(),
            AnnotationTool.Mosaic or AnnotationTool.Blur => new Rectangle { StrokeDashArray = [3, 2] },
            _ => new Rectangle()
        };
        shape.Stroke = new SolidColorBrush(color);
        if (tool is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse)
            shape.Fill = new SolidColorBrush(color);
        if (tool == AnnotationTool.HighlighterRectangle)
        {
            shape.Stroke = null;
            shape.Fill = new SolidColorBrush(color);
        }
        else if (tool == AnnotationTool.FilledArrow)
        {
            shape.Fill = new SolidColorBrush(color);
            shape.StrokeThickness = 0.5;
        }
        else if (tool == AnnotationTool.OutlineArrow)
        {
            shape.Fill = Brushes.Transparent;
            shape.StrokeThickness = Math.Max(1, _drawingWidth / 2);
        }
        else
        {
            shape.StrokeThickness = tool == AnnotationTool.Highlighter ? Math.Max(10, _drawingWidth * 3) : _drawingWidth;
        }
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.Opacity = IsHighlighterTool(tool) ? Math.Min(0.45, _drawingOpacity) : _drawingOpacity;
        if (shape is Polyline poly) poly.Points.Add(point);
        Canvas.SetLeft(shape, point.X);
        Canvas.SetTop(shape, point.Y);
        return shape;
    }

    private void UpdateDrawingElement(FrameworkElement? element, Point start, Point end)
    {
        if (element is Line line)
        {
            Canvas.SetLeft(line, 0); Canvas.SetTop(line, 0);
            line.X1 = start.X; line.Y1 = start.Y; line.X2 = end.X; line.Y2 = end.Y;
        }
        else if (element is System.Windows.Shapes.Path arrow && IsArrowTool(_tool))
        {
            Canvas.SetLeft(arrow, 0); Canvas.SetTop(arrow, 0);
            arrow.Data = CreateArrowGeometry(start, end, _tool, _drawingWidth);
        }
        else if (element is Polyline freehand)
        {
            Canvas.SetLeft(freehand, 0); Canvas.SetTop(freehand, 0);
            freehand.Points.Add(end);
        }
        else if (element is Shape shape)
        {
            Canvas.SetLeft(shape, Math.Min(start.X, end.X)); Canvas.SetTop(shape, Math.Min(start.Y, end.Y));
            shape.Width = Math.Abs(end.X - start.X); shape.Height = Math.Abs(end.Y - start.Y);
        }
    }

    private static Geometry CreateArrowGeometry(Point start, Point end, AnnotationTool tool, double width)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 2) return Geometry.Empty;

        var ux = dx / length;
        var uy = dy / length;
        var px = -uy;
        var py = ux;
        var headLength = Math.Min(Math.Max(20, width * 6), length * 0.58);
        var headHalfWidth = Math.Min(Math.Max(10, width * 3), length * 0.32);
        var headBase = new Point(end.X - ux * headLength, end.Y - uy * headLength);
        var upperHead = new Point(headBase.X + px * headHalfWidth, headBase.Y + py * headHalfWidth);
        var lowerHead = new Point(headBase.X - px * headHalfWidth, headBase.Y - py * headHalfWidth);

        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        if (tool == AnnotationTool.Arrow)
        {
            context.BeginFigure(start, false, false);
            context.LineTo(end, true, false);
            context.BeginFigure(upperHead, false, false);
            context.LineTo(end, true, false);
            context.LineTo(lowerHead, true, false);
            return geometry;
        }

        var shaftHalfWidth = Math.Max(1.5, width);
        var upperStart = new Point(start.X + px * shaftHalfWidth, start.Y + py * shaftHalfWidth);
        var lowerStart = new Point(start.X - px * shaftHalfWidth, start.Y - py * shaftHalfWidth);
        var upperShaftEnd = new Point(headBase.X + px * shaftHalfWidth, headBase.Y + py * shaftHalfWidth);
        var lowerShaftEnd = new Point(headBase.X - px * shaftHalfWidth, headBase.Y - py * shaftHalfWidth);
        context.BeginFigure(upperStart, true, true);
        context.LineTo(upperShaftEnd, true, false);
        context.LineTo(upperHead, true, false);
        context.LineTo(end, true, false);
        context.LineTo(lowerHead, true, false);
        context.LineTo(lowerShaftEnd, true, false);
        context.LineTo(lowerStart, true, false);
        return geometry;
    }

    private FrameworkElement CreateFilteredRegion(PixelRect region, bool blur)
    {
        region = region.ClampTo(_selection);
        if (region.IsEmpty) region = new PixelRect((int)_start.X, (int)_start.Y, 1, 1);
        var stride = region.Width * 4;
        var pixels = new byte[stride * region.Height];
        _bitmap.CopyPixels(new Int32Rect(region.X, region.Y, region.Width, region.Height), pixels, stride, 0);
        if (blur) ApplyBoxBlur(pixels, region.Width, region.Height, stride, Math.Max(1, _filterStrength / 2));
        else ApplyMosaic(pixels, region.Width, region.Height, stride, _filterStrength);
        var source = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var image = new Image { Source = source, Width = region.Width, Height = region.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, region.X); Canvas.SetTop(image, region.Y);
        return image;
    }

    private static void ApplyMosaic(byte[] pixels, int width, int height, int stride, int block)
    {
        block = Math.Clamp(block, 2, 64);
        for (var y = 0; y < height; y += block)
        for (var x = 0; x < width; x += block)
        {
            var sample = y * stride + x * 4;
            for (var yy = y; yy < Math.Min(height, y + block); yy++)
            for (var xx = x; xx < Math.Min(width, x + block); xx++)
            {
                var target = yy * stride + xx * 4;
                Array.Copy(pixels, sample, pixels, target, 4);
            }
        }
    }

    private static void ApplyBoxBlur(byte[] pixels, int width, int height, int stride, int radius)
    {
        var original = (byte[])pixels.Clone();
        radius = Math.Clamp(radius, 1, 16);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var count = 0; var b = 0; var g = 0; var r = 0; var a = 0;
            for (var yy = Math.Max(0, y - radius); yy <= Math.Min(height - 1, y + radius); yy += 2)
            for (var xx = Math.Max(0, x - radius); xx <= Math.Min(width - 1, x + radius); xx += 2)
            {
                var i = yy * stride + xx * 4;
                b += original[i]; g += original[i + 1]; r += original[i + 2]; a += original[i + 3]; count++;
            }
            var o = y * stride + x * 4;
            pixels[o] = (byte)(b / count); pixels[o + 1] = (byte)(g / count);
            pixels[o + 2] = (byte)(r / count); pixels[o + 3] = (byte)(a / count);
        }
    }

    private void BeginText(Point point)
    {
        var fontFamily = FontFamilyPicker.SelectedItem as FontFamily ?? new FontFamily("Microsoft JhengHei UI");
        var fontSize = SelectedFontSize();
        var editor = new TextBox
        {
            MinWidth = 120, FontFamily = fontFamily, FontSize = fontSize,
            FontWeight = BoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = ItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(_drawingColor), Opacity = _drawingOpacity,
            Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)), AcceptsReturn = true
        };
        Canvas.SetLeft(editor, point.X); Canvas.SetTop(editor, point.Y);
        AnnotationCanvas.Children.Add(editor);
        editor.Focus();
        editor.LostKeyboardFocus += Commit;
        editor.KeyDown += (_, e) => { if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; Commit(editor, e); } };

        void Commit(object? sender, RoutedEventArgs args)
        {
            if (!AnnotationCanvas.Children.Contains(editor)) return;
            AnnotationCanvas.Children.Remove(editor);
            if (string.IsNullOrWhiteSpace(editor.Text)) return;
            var text = new TextBlock
            {
                Text = editor.Text, FontFamily = editor.FontFamily, FontSize = editor.FontSize,
                FontWeight = editor.FontWeight, FontStyle = editor.FontStyle,
                Foreground = editor.Foreground, Opacity = editor.Opacity, Background = Brushes.Transparent
            };
            Canvas.SetLeft(text, point.X); Canvas.SetTop(text, point.Y);
            AddAnnotationCommand(text);
        }
    }

    private void AddAnnotationCommand(FrameworkElement element)
    {
        if (AnnotationCanvas.Children.Contains(element)) AnnotationCanvas.Children.Remove(element);
        _undo.Execute(new DelegateCommand(
            () => { if (!AnnotationCanvas.Children.Contains(element)) AnnotationCanvas.Children.Add(element); if (!_annotations.Contains(element)) _annotations.Add(element); },
            () => { AnnotationCanvas.Children.Remove(element); _annotations.Remove(element); }));
        UpdateHistoryButtons();
    }

    private void EraseAt(Point point)
    {
        var element = FindAnnotationAt(point);
        if (element is null) return;
        _undo.Execute(new DelegateCommand(
            () => { AnnotationCanvas.Children.Remove(element); _annotations.Remove(element); },
            () => { AnnotationCanvas.Children.Add(element); _annotations.Add(element); }));
        UpdateHistoryButtons();
    }

    private FrameworkElement? FindAnnotationAt(Point point)
    {
        foreach (var element in _annotations.AsEnumerable().Reverse())
        {
            if (!element.IsVisible) continue;
            try
            {
                var bounds = element.TransformToAncestor(AnnotationCanvas).TransformBounds(VisualTreeHelper.GetDescendantBounds(element));
                bounds.Inflate(Math.Max(5, _drawingWidth), Math.Max(5, _drawingWidth));
                if (bounds.Contains(point)) return element;
            }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private BitmapSource RenderSelection()
    {
        if (_selection.IsEmpty) throw new InvalidOperationException("尚未選取截圖範圍。");
        var rect = new Int32Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height);
        var baseCrop = new CroppedBitmap(_bitmap, rect);
        AnnotationCanvas.Measure(new Size(_frame.Bounds.Width, _frame.Bounds.Height));
        AnnotationCanvas.Arrange(new Rect(0, 0, _frame.Bounds.Width, _frame.Bounds.Height));
        var annotations = new RenderTargetBitmap(_frame.Bounds.Width, _frame.Bounds.Height, 96, 96, PixelFormats.Pbgra32);
        annotations.Render(AnnotationCanvas);
        var annotationCrop = new CroppedBitmap(annotations, rect);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(baseCrop, new Rect(0, 0, _selection.Width, _selection.Height));
            drawing.DrawImage(annotationCrop, new Rect(0, 0, _selection.Width, _selection.Height));
        }
        var result = new RenderTargetBitmap(_selection.Width, _selection.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    private void CopyAndClose()
    {
        try { _clipboard.SetPng(ClipboardService.EncodePng(RenderSelection())); DialogResult = true; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { MessageBox.Show(this, $"無法複製圖片：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var dialog = new SaveFileDialog
            {
                InitialDirectory = _outputDirectory, Filter = "PNG 圖片|*.png|JPEG 圖片|*.jpg",
                FileName = $"MySnip-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllBytes(dialog.FileName, BitmapFactory.Encode(RenderSelection(), System.IO.Path.GetExtension(dialog.FileName)));
            DialogResult = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { MessageBox.Show(this, $"無法儲存圖片：{ex.Message}", "MySnip", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void UpdateHover(Point point)
    {
        var global = ToGlobal(ToPixel(point));
        var cursor = new System.Drawing.Point(global.X, global.Y);
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).Bounds;
        var screenBounds = new PixelRect(screen.X, screen.Y, screen.Width, screen.Height);
        // Match Snipaste-style exploration: prefer the smallest UI element under the
        // cursor, use a reasonably sized window only as an immediate fallback, and
        // never highlight a candidate occupying over half of the current screen.
        var candidate = CaptureCandidatePicker.PickBestCompact(
            _candidates, global, _frame.Bounds, screenBounds);
        _hoverCandidate = candidate is null ? null : ToLocal(candidate.Value.Bounds);
        UpdateSelectionVisual();
    }

    private void UpdateMagnifier(Point point)
    {
        if (_isSelectionCommitted)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        const int radius = 7;
        const int side = radius * 2 + 1;
        var global = ToGlobal(ToPixel(point));
        var pixels = CapturedPixelSampler.SampleBgraSquare(_frame, global, radius);
        var source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
        source.Freeze();
        MagnifierPixels.Source = source;

        var color = CapturedPixelSampler.Sample(_frame, global);
        MagnifierCoordinate.Text = $"({global.X}, {global.Y})";
        MagnifierColor.Text = _showHex ? color.ToHexText() : color.ToRgbText();

        var location = MagnifierPlacement.Place(ToPixel(point), new PixelSize(154, 184), CanvasBounds);
        Canvas.SetLeft(Magnifier, location.X);
        Canvas.SetTop(Magnifier, location.Y);
        Magnifier.Visibility = Visibility.Visible;
    }

    private void SelectSuggestedWindow()
    {
        var candidate = CaptureCandidatePicker.PickBest(_candidates, ToGlobal(ToPixel(_currentPoint)),
            _frame.Bounds, CaptureCandidateKind.TopLevelWindow);
        if (candidate is null) return;
        CommitSelection(ToLocal(candidate.Value.Bounds));
    }

    private void SelectCurrentScreen()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Control.MousePosition);
        CommitSelection(ToLocal(new PixelRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height).ClampTo(_frame.Bounds)));
    }

    private void CommitSelection(PixelRect selection)
    {
        _selection = selection.ClampTo(CanvasBounds);
        _hoverCandidate = null;
        _isSelectionCommitted = !_selection.IsEmpty;
        Magnifier.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _showHex = true;
            UpdateMagnifier(_currentPoint);
            return;
        }
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        if (e.Key == Key.Enter || (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CopyAndClose(); return; }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save(); return; }
        if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _createSticker(RenderSelection()); DialogResult = true; return; }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _undo.Undo(); UpdateHistoryButtons(); return; }
        if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _undo.Redo(); UpdateHistoryButtons(); return; }
        if (e.Key == Key.D1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _tool = _lastShapeTool; UpdateToolUi(); return; }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.None) { _tool = AnnotationTool.Select; Cursor = Cursors.Arrow; UpdateToolUi(); return; }
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.None) { _tool = AnnotationTool.Eraser; Cursor = Cursors.Cross; UpdateToolUi(); return; }
        if (e.Key == Key.W) { SelectSuggestedWindow(); return; }
        if (e.Key == Key.F) { SelectCurrentScreen(); return; }
        if (_isSelectionCommitted && !_selection.IsEmpty && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            _selection = _selection.MoveBy(e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0,
                e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0, CanvasBounds);
            UpdateSelectionVisual(); e.Handled = true;
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.LeftShift or Key.RightShift)) return;
        _showHex = false;
        UpdateMagnifier(_currentPoint);
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<AnnotationTool>(tag, out var tool)) return;

        if (ReferenceEquals(sender, ShapeToolButton)) tool = _lastShapeTool;
        else if (ReferenceEquals(sender, LineToolButton)) tool = _lastLineTool;
        else if (ReferenceEquals(sender, HighlighterToolButton)) tool = _lastHighlighterTool;
        else if (ReferenceEquals(sender, FilterToolButton)) tool = _lastFilterTool;

        _tool = tool;
        if (tool is AnnotationTool.Rectangle or AnnotationTool.FilledRectangle or AnnotationTool.Ellipse or AnnotationTool.FilledEllipse)
            _lastShapeTool = tool;
        if (IsLineTool(tool)) _lastLineTool = tool;
        if (IsHighlighterTool(tool)) _lastHighlighterTool = tool;
        if (tool is AnnotationTool.Mosaic or AnnotationTool.Blur) _lastFilterTool = tool;
        Cursor = tool == AnnotationTool.Select ? Cursors.Arrow : Cursors.Cross;
        UpdateToolUi();
    }

    private void ShapeFill_Click(object sender, RoutedEventArgs e)
    {
        var filled = ReferenceEquals(sender, FilledShapeOption);
        var ellipse = _lastShapeTool is AnnotationTool.Ellipse or AnnotationTool.FilledEllipse;
        _tool = (ellipse, filled) switch
        {
            (true, true) => AnnotationTool.FilledEllipse,
            (true, false) => AnnotationTool.Ellipse,
            (false, true) => AnnotationTool.FilledRectangle,
            _ => AnnotationTool.Rectangle
        };
        _lastShapeTool = _tool;
        Cursor = Cursors.Cross;
        UpdateToolUi();
    }

    private void ShapeGeometry_Click(object sender, RoutedEventArgs e)
    {
        var ellipse = ReferenceEquals(sender, EllipseOption);
        var filled = _lastShapeTool is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse;
        _tool = (ellipse, filled) switch
        {
            (true, true) => AnnotationTool.FilledEllipse,
            (true, false) => AnnotationTool.Ellipse,
            (false, true) => AnnotationTool.FilledRectangle,
            _ => AnnotationTool.Rectangle
        };
        _lastShapeTool = _tool;
        Cursor = Cursors.Cross;
        UpdateToolUi();
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingStyleControls) return;
        _drawingWidth = WidthSlider.Value;
        _drawingOpacity = OpacitySlider.Value;
        _filterStrength = (int)Math.Round(FilterStrengthSlider.Value);
        UpdateStyleLabels();
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e) => UpdateToolUi();

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        _drawingColor = (Color)ColorConverter.ConvertFromString(color);
        UpdateColorPreview();
    }

    private void MoreColors_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_drawingColor.R, _drawingColor.G, _drawingColor.B)
        };
        var owner = new NativeWindowOwner(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        _drawingColor = Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B);
        UpdateColorPreview();
    }

    private void FontFamilyPicker_DropDownOpened(object sender, EventArgs e)
    {
        if (_fontsLoaded) return;
        var selected = (FontFamilyPicker.SelectedItem as FontFamily)?.Source ?? "Microsoft JhengHei UI";
        _updatingStyleControls = true;
        FontFamilyPicker.Items.Clear();
        foreach (var font in Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase))
            FontFamilyPicker.Items.Add(font);
        FontFamilyPicker.SelectedItem = FontFamilyPicker.Items.OfType<FontFamily>()
            .FirstOrDefault(font => string.Equals(font.Source, selected, StringComparison.CurrentCultureIgnoreCase)) ??
            FontFamilyPicker.Items.OfType<FontFamily>().FirstOrDefault();
        _fontsLoaded = true;
        _updatingStyleControls = false;
    }

    private void UpdateToolUi()
    {
        ShapeToolButton.IsChecked = IsShapeTool(_tool);
        LineToolButton.IsChecked = IsLineTool(_tool);
        PenToolButton.IsChecked = _tool == AnnotationTool.Pen;
        HighlighterToolButton.IsChecked = IsHighlighterTool(_tool);
        FilterToolButton.IsChecked = _tool is AnnotationTool.Mosaic or AnnotationTool.Blur;
        TextToolButton.IsChecked = _tool == AnnotationTool.Text;
        EraserToolButton.IsChecked = _tool == AnnotationTool.Eraser;
        OutlineShapeOption.IsChecked = _tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse;
        FilledShapeOption.IsChecked = _tool is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse;
        RectangleOption.IsChecked = _tool is AnnotationTool.Rectangle or AnnotationTool.FilledRectangle;
        EllipseOption.IsChecked = _tool is AnnotationTool.Ellipse or AnnotationTool.FilledEllipse;
        LineOption.IsChecked = _tool == AnnotationTool.Line;
        ArrowOption.IsChecked = _tool == AnnotationTool.Arrow;
        OutlineArrowOption.IsChecked = _tool == AnnotationTool.OutlineArrow;
        FilledArrowOption.IsChecked = _tool == AnnotationTool.FilledArrow;
        HighlighterLineOption.IsChecked = _tool == AnnotationTool.Highlighter;
        HighlighterRectangleOption.IsChecked = _tool == AnnotationTool.HighlighterRectangle;
        MosaicOption.IsChecked = _tool == AnnotationTool.Mosaic;
        BlurOption.IsChecked = _tool == AnnotationTool.Blur;

        ShapeOptions.Visibility = Show(IsShapeTool(_tool));
        LineOptions.Visibility = Show(IsLineTool(_tool));
        HighlighterOptions.Visibility = Show(IsHighlighterTool(_tool));
        FilterOptions.Visibility = Show(_tool is AnnotationTool.Mosaic or AnnotationTool.Blur);
        TextOptions.Visibility = Show(_tool == AnnotationTool.Text);

        var supportsStroke = IsShapeTool(_tool) || IsLineTool(_tool) || _tool == AnnotationTool.Pen || IsHighlighterTool(_tool);
        var supportsColor = supportsStroke || _tool == AnnotationTool.Text;
        StrokeOptions.Visibility = Show(supportsStroke);
        ColorOptions.Visibility = Show(supportsColor);
        OpacityOptions.Visibility = Show(supportsColor);
        ContextToolbar.Visibility = Show(supportsColor || _tool is AnnotationTool.Mosaic or AnnotationTool.Blur);
        UpdateColorPreview();

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsShapeTool(AnnotationTool tool) => tool is AnnotationTool.Rectangle or AnnotationTool.FilledRectangle or
        AnnotationTool.Ellipse or AnnotationTool.FilledEllipse;

    private static bool IsArrowTool(AnnotationTool tool) => tool is AnnotationTool.Arrow or AnnotationTool.OutlineArrow or AnnotationTool.FilledArrow;

    private static bool IsLineTool(AnnotationTool tool) => tool == AnnotationTool.Line || IsArrowTool(tool);

    private static bool IsHighlighterTool(AnnotationTool tool) => tool is AnnotationTool.Highlighter or AnnotationTool.HighlighterRectangle;

    private void UpdateStyleLabels()
    {
        WidthValue.Text = $"{Math.Round(_drawingWidth):0}";
        OpacityValue.Text = $"{Math.Round(_drawingOpacity * 100):0}%";
        FilterStrengthValue.Text = _filterStrength.ToString();
    }

    private void UpdateColorPreview()
    {
        if (CurrentColorPreview is null) return;
        CurrentColorPreview.Background = new SolidColorBrush(_drawingColor);
        foreach (var button in ColorPalette.Children.OfType<Button>())
        {
            var selected = button.Tag is string color &&
                string.Equals(color, _drawingColor.ToString(), StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(0, 122, 255)) :
                new SolidColorBrush(Color.FromRgb(119, 119, 119));
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(1);
        }
    }

    private double SelectedFontSize() =>
        FontSizePicker.SelectedItem is ComboBoxItem item && double.TryParse(item.Content?.ToString(), out var size)
            ? size : 22;

    private void UpdateHistoryButtons()
    {
        if (UndoButton is null || RedoButton is null) return;
        UndoButton.IsEnabled = _undo.CanUndo;
        RedoButton.IsEnabled = _undo.CanRedo;
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { _undo.Undo(); UpdateHistoryButtons(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { _undo.Redo(); UpdateHistoryButtons(); }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyAndClose();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void Sticker_Click(object sender, RoutedEventArgs e) { _createSticker(RenderSelection()); DialogResult = true; }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class NativeWindowOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, _frame.Bounds.Width), Math.Clamp(point.Y, 0, _frame.Bounds.Height));
    private Point ClampToSelectionIfDrawing(Point point) => _dragMode == DragMode.Draw
        ? new Point(Math.Clamp(point.X, _selection.Left, _selection.Right), Math.Clamp(point.Y, _selection.Top, _selection.Bottom)) : point;
    private static PixelPoint ToPixel(Point point) => new((int)Math.Round(point.X), (int)Math.Round(point.Y));
    private PixelPoint ToGlobal(PixelPoint local) => new(local.X + _frame.Bounds.X, local.Y + _frame.Bounds.Y);
    private static double GetLeft(FrameworkElement element) => double.IsNaN(Canvas.GetLeft(element)) ? 0 : Canvas.GetLeft(element);
    private static double GetTop(FrameworkElement element) => double.IsNaN(Canvas.GetTop(element)) ? 0 : Canvas.GetTop(element);
}
