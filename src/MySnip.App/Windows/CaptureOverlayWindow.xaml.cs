using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Runtime.InteropServices;
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
    private enum DragMode { None, CreateSelection, MoveSelection, ResizeLeft, ResizeTop, ResizeRight, ResizeBottom, ResizeTopLeft, ResizeTopRight, ResizeBottomLeft, ResizeBottomRight, Draw, DrawText, MoveElement, MoveInlineText, ResizeAnnotation, SelectAnnotations }

    private CapturedFrame _frame;
    private BitmapSource _bitmap;
    private readonly HashSet<CaptureCandidate> _candidates;
    private readonly IClipboardService _clipboard;
    private readonly Action<BitmapSource> _createSticker;
    private readonly string _outputDirectory;
    private readonly PixelRect[] _displays;
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
        _displays = ScreenCaptureService.GetDisplayBounds().Select(ToLocal).ToArray();
        _candidates = candidates.ToHashSet();
        _clipboard = clipboard;
        _createSticker = createSticker;
        _outputDirectory = outputDirectory;
        _drawingColor = (Color)ColorConverter.ConvertFromString(defaults.Color);
        _drawingWidth = defaults.StrokeWidth;
        _drawingOpacity = defaults.Opacity;
        _bitmap = BitmapFactory.FromFrame(frame);
        Screenshot.Source = _bitmap;
        CreateSelectionHandles();
        ConfigureCanvas();
        ConfigureStyleControls(defaults);
        UpdateSelectionVisual();
        SourceInitialized += OnSourceInitialized;
        Closing += (_, _) => SaveCanceledCapture();
        Closed += OnClosed;
        Loaded += (_, _) =>
        {
            ApplyPhysicalOverlayBounds();
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
        var annotationFill = new SolidColorBrush(Color.FromRgb(39, 167, 231));
        for (var i = 0; i < _annotationResizeHandles.Length; i++)
        {
            var handle = new Ellipse { Width = 7, Height = 7, Fill = annotationFill, Stroke = Brushes.White,
                StrokeThickness = 1, Visibility = Visibility.Collapsed };
            _annotationResizeHandles[i] = handle;
            MaskCanvas.Children.Add(handle);
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
        if (message == NativeMethods.WmGetMinMaxInfo)
        {
            var info = Marshal.PtrToStructure<NativeMethods.MinMaxInfo>(lParam);
            info.MaxTrackSize.X = Math.Max(info.MaxTrackSize.X, _frame.Bounds.Width);
            info.MaxTrackSize.Y = Math.Max(info.MaxTrackSize.Y, _frame.Bounds.Height);
            Marshal.StructureToPtr(info, lParam, false);
        }
        else if (message == NativeMethods.WmDpiChanged && !_nativeLayoutUpdatePending)
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
                NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged | NativeMethods.SwpNoSendChanging))
        {
            throw new InvalidOperationException("無法將截圖覆蓋視窗定位到虛擬桌面。");
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        RemoveTextCaret();
        _share?.Dispose();
        SaveAdvancedChanges();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
    }

    private PixelRect ToLocal(PixelRect global) => new(global.X - _frame.Bounds.X, global.Y - _frame.Bounds.Y, global.Width, global.Height);
    private PixelRect CanvasBounds => new(0, 0, _frame.Bounds.Width, _frame.Bounds.Height);
    private PixelRect DisplayAt(PixelPoint point) => DesktopGeometry.DisplayAt(_displays, point, CanvasBounds);

    private void Input_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pointer = PointerPosition(e);
        var activeHandle = HitAnnotationHandle(pointer);
        if (activeHandle is { Element: OutlinedAnnotationText target } && IsTextDeleteHandle(target, activeHandle.Value.Direction))
        {
            _currentPoint = pointer;
            DeleteTextFromHandle(target);
            e.Handled = true;
            return;
        }
        if (activeHandle is null && _textEditor is not null && IsInTextFrame(pointer))
        {
            StartInlineTextMove(pointer, -1);
            return;
        }
        _resumeTextAfterResize = _textEditor is not null && activeHandle is { Element: OutlinedAnnotationText } &&
            !string.IsNullOrWhiteSpace(_textEditor.Text);
        FinishTextEditor();
        Focus();
        _start = pointer;
        _currentPoint = _start;
        _startSelection = _selection;
        InputCanvas.CaptureMouse();

        if (!_isSelectionCommitted)
        {
            if (!_editingSticker) ReplayedScreenshot.Source = null;
            _mouseDownCandidate = _hoverCandidate;
            _hoverCandidate = null;
            _dragMode = DragMode.CreateSelection;
            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            UpdateSelectionVisual();
            return;
        }

        if (HitAnnotationHandle(_start) is { } handle)
        {
            _annotationResize = (handle.Element, AnnotationResizeBounds(handle.Element), handle.Direction,
                handle.Element is OutlinedAnnotationText textTarget ? textTarget.FontSize : 0);
            _dragMode = DragMode.ResizeAnnotation;
            InputCanvas.Cursor = AnnotationResizeCursor(handle.Element, handle.Direction);
            return;
        }

        var inside = _selection.Contains(ToPixel(_start));
        var hit = _tool != AnnotationTool.Eraser && _lineStrip is null
            ? FindAnnotationAt(_start, precise: _tool != AnnotationTool.Select) : null;
        if (e.ClickCount == 2 && hit is OutlinedAnnotationText text)
        {
            InputCanvas.ReleaseMouseCapture();
            SelectAnnotations([text]);
            EditAnnotationText(text);
            return;
        }
        if (hit is not null && StartAnnotationSelection(_start, hit)) return;

        if (e.ClickCount == 2 && _tool == AnnotationTool.Select && inside)
        {
            InputCanvas.ReleaseMouseCapture();
            CopyAndClose();
            return;
        }

        if (_tool == AnnotationTool.Select)
        {
            if (StartAnnotationSelection(_start, null)) return;

            if (_editingSticker)
            {
                _dragMode = DragMode.None;
                InputCanvas.ReleaseMouseCapture();
                return;
            }
            _dragMode = HitSelectionEdge(_start);
            if (_dragMode != DragMode.CreateSelection) return;

            ReplayedScreenshot.Source = null;
            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            _isSelectionCommitted = false;
            Toolbar.Visibility = Visibility.Collapsed;
            UpdateMagnifier(_start);
            return;
        }

        SelectAnnotations([]);
        if (_selection.IsEmpty || !inside && !CanDrawOutsideSelection(_tool))
        {
            if (_editingSticker) { InputCanvas.ReleaseMouseCapture(); return; }
            ReplayedScreenshot.Source = null;
            _dragMode = DragMode.CreateSelection;
            _isSelectionCommitted = false;
            _selection = new PixelRect((int)_start.X, (int)_start.Y, 0, 0);
            Toolbar.Visibility = Visibility.Collapsed;
            UpdateMagnifier(_start);
            return;
        }

        if (_tool == AnnotationTool.Eraser && !_roundEraser)
        {
            EraseAt(_start);
            _dragMode = DragMode.None;
            InputCanvas.ReleaseMouseCapture();
            return;
        }

        if (_tool == AnnotationTool.Number)
        {
            AddNumber(_start);
            InputCanvas.ReleaseMouseCapture();
            return;
        }

        if (_tool == AnnotationTool.LineStrip)
        {
            AddLineStripPoint(_start, e.ClickCount > 1);
            InputCanvas.ReleaseMouseCapture();
            return;
        }

        if (_tool == AnnotationTool.Text)
        {
            StartTextBox();
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
        var point = ClampToSelectionIfDrawing(PointerPosition(e));
        _currentPoint = point;
        UpdateMagnifier(point);
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (!_isSelectionCommitted)
            {
                UpdateHover(point);
                InputCanvas.Cursor = Cursors.Cross;
                return;
            }
            UpdatePointerCursor(point);
            return;
        }

        switch (_dragMode)
        {
            case DragMode.CreateSelection:
                _selection = DragSelection(point);
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
            case DragMode.DrawText: UpdateTextBoxPreview(point); break;
            case DragMode.Draw:
                UpdateDrawingElement(_activeElement, _start, point, _tool, _drawingWidth);
                break;
            case DragMode.MoveElement: UpdateAnnotationMove(point); break;
            case DragMode.MoveInlineText: MoveInlineText(point); break;
            case DragMode.ResizeAnnotation: UpdateAnnotationResize(point); break;
            case DragMode.SelectAnnotations: UpdateMarquee(point); break;
        }
    }

    private void Input_LostMouseCapture(object sender, MouseEventArgs e)
    {
        CancelAnnotationResize();
        if (_dragMode == DragMode.MoveInlineText) EndInlineTextMove(_start, cancel: true);
        if (_dragMode != DragMode.DrawText) return;
        _dragMode = DragMode.None;
        RemoveTextBoxPreview();
    }

    private void Input_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _currentPoint = PointerPosition(e);
        var point = ClampToSelectionIfDrawing(_currentPoint);
        if (_dragMode == DragMode.MoveInlineText)
        {
            EndInlineTextMove(point);
            return;
        }
        if (_dragMode == DragMode.ResizeAnnotation)
        {
            EndAnnotationResize(point);
            _dragMode = DragMode.None;
            InputCanvas.ReleaseMouseCapture();
            UpdatePointerCursor(point);
            return;
        }
        if (_dragMode == DragMode.DrawText)
        {
            _dragMode = DragMode.None;
            InputCanvas.ReleaseMouseCapture();
            OpenNewTextBox(point);
            return;
        }
        InputCanvas.ReleaseMouseCapture();
        if (_dragMode == DragMode.CreateSelection && !_isSelectionCommitted)
        {
            var dragged = DragSelection(point);
            _selection = dragged.Width >= 3 || dragged.Height >= 3
                ? dragged
                : _mouseDownCandidate ?? default;
            _isSelectionCommitted = !_selection.IsEmpty;
            _mouseDownCandidate = null;
            if (_isSelectionCommitted)
            {
                Magnifier.Visibility = Visibility.Collapsed;
                QueueCommandOutput();
            }
            else
            {
                UpdateHover(point);
                UpdateMagnifier(point);
            }
        }
        else if (_dragMode == DragMode.Draw && _activeElement is not null)
        {
            var element = _activeElement;
            if (_tool == AnnotationTool.Eraser)
            {
                AnnotationCanvas.Children.Remove(element);
                EraseEllipse(new Rect(_start, point));
            }
            else
            {
                if (IsFilterTool(_tool))
                {
                    AnnotationCanvas.Children.Remove(element);
                    element = CreateFilteredRegion(PixelRect.FromPoints(ToPixel(_start), ToPixel(point)), _tool);
                    AddAnnotationCommand(element);
                }
                else
                {
                    UpdateDrawingElement(element, _start, point, _tool, _drawingWidth);
                    if (IsResizableShape(element) && (element.Width < 1 || element.Height < 1))
                        AnnotationCanvas.Children.Remove(element);
                    else AddAnnotationCommand(element);
                }
            }
        }
        else if (_dragMode == DragMode.MoveElement) EndAnnotationMove(point);
        else if (_dragMode == DragMode.SelectAnnotations) CommitMarquee(point);
        else RefreshAnnotationFilters();

        _activeElement = null;
        _dragMode = DragMode.None;
        UpdateSelectionVisual();
        UpdatePointerCursor(_currentPoint);
    }

    private void UpdatePointerCursor(Point point)
    {
        InputCanvas.Cursor = HitAnnotationHandle(point) is { } handle
            ? IsTextDeleteHandle(handle.Element, handle.Direction) ? Cursors.Hand : AnnotationResizeCursor(handle.Element, handle.Direction)
            : IsInTextFrame(point) ? Cursors.SizeAll
            : _isSelectionCommitted && !_editingSticker && _tool == AnnotationTool.Select ? CursorFor(HitSelectionEdge(point))
            : _tool == AnnotationTool.Select && _isSelectionCommitted ? Cursors.Arrow : Cursors.Cross;
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
        var scale = _useDip ? DisplayScale() : (X: 1d, Y: 1d);
        SizeLabel.Text = s.IsEmpty ? string.Empty : $"{s.Width / scale.X:0.##} × {s.Height / scale.Y:0.##} {(_useDip ? "DIP" : "px")}";
        SizeLabel.Visibility = s.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        var display = DisplayAt(ToPixel(_currentPoint));
        HelpText.Visibility = !_isSelectionCommitted && _dragMode == DragMode.None ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(HelpText, display.Left + 18);
        Canvas.SetTop(HelpText, display.Top + 16);
        SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeLabel, Math.Clamp(s.Left, display.Left, Math.Max(display.Left, display.Right - SizeLabel.DesiredSize.Width)));
        Canvas.SetTop(SizeLabel, Math.Clamp(s.Top - 26, display.Top, Math.Max(display.Top, display.Bottom - SizeLabel.DesiredSize.Height)));
        var showCommittedControls = _isSelectionCommitted && !_selection.IsEmpty;
        UpdateHandlePositions(s, showCommittedControls);
        UpdateAnnotationOutlines();
        Toolbar.Visibility = showCommittedControls ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.MaxWidth = Math.Max(1, display.Width);
        Toolbar.Measure(new Size(display.Width, double.PositiveInfinity));
        var toolbarLeft = Math.Clamp(s.Left, display.Left, Math.Max(display.Left, display.Right - Toolbar.DesiredSize.Width));
        var below = s.Bottom + 8;
        var toolbarTop = below + Toolbar.DesiredSize.Height <= display.Bottom ? below : s.Top - Toolbar.DesiredSize.Height - 8;
        toolbarTop = Math.Clamp(toolbarTop, display.Top, Math.Max(display.Top, display.Bottom - Toolbar.DesiredSize.Height));
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

    private Shape CreateDrawingElement(AnnotationTool tool, Point point)
    {
        if (IsFilterTool(tool) && EllipseFilter.IsChecked == true)
            return new Ellipse { Stroke = new SolidColorBrush(_drawingColor), StrokeDashArray = [3, 2], StrokeThickness = _drawingWidth };
        return CreateDrawingElement(tool, point, _drawingColor, _drawingWidth, _drawingOpacity, DashedStroke.IsChecked == true);
    }

    private static Shape CreateDrawingElement(AnnotationTool tool, Point point, Color color, double width, double opacity, bool dashed)
    {
        Shape shape = tool switch
        {
            AnnotationTool.Rectangle => new Rectangle(),
            AnnotationTool.FilledRectangle => new Rectangle(),
            AnnotationTool.RoundedRectangle => new Rectangle { RadiusX = 12, RadiusY = 12 },
            AnnotationTool.Ellipse => new Ellipse(),
            AnnotationTool.FilledEllipse => new Ellipse(),
            AnnotationTool.Line or AnnotationTool.Highlighter => new Line(),
            AnnotationTool.Arrow or AnnotationTool.OutlineArrow or AnnotationTool.FilledArrow or AnnotationTool.DoubleArrow => new System.Windows.Shapes.Path(),
            AnnotationTool.Pen or AnnotationTool.LineStrip => new Polyline(),
            AnnotationTool.HighlighterRectangle => new Rectangle(),
            AnnotationTool.HighlighterEllipse => new Ellipse(),
            AnnotationTool.Eraser => new Ellipse { StrokeDashArray = [3, 2] },
            AnnotationTool.Magnify => new Ellipse { StrokeDashArray = [3, 2] },
            AnnotationTool.Mosaic or AnnotationTool.Blur => new Rectangle { StrokeDashArray = [3, 2] },
            _ => new Rectangle()
        };
        shape.Stroke = new SolidColorBrush(color);
        if (tool is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse)
            shape.Fill = new SolidColorBrush(color);
        if (tool is AnnotationTool.HighlighterRectangle or AnnotationTool.HighlighterEllipse)
        {
            shape.Stroke = null;
            shape.Fill = new SolidColorBrush(color);
        }
        else if (tool == AnnotationTool.FilledArrow) shape.Fill = new SolidColorBrush(color);
        else if (tool == AnnotationTool.OutlineArrow) shape.Fill = Brushes.Transparent;
        shape.StrokeThickness = StrokeWidth(tool, width);
        shape.StrokeStartLineCap = shape.StrokeEndLineCap = PenLineCap.Round;
        shape.StrokeLineJoin = PenLineJoin.Round;
        if (dashed && (IsShapeTool(tool) || IsLineTool(tool) || tool == AnnotationTool.Pen))
            shape.StrokeDashArray = [3, 2];
        if (tool is AnnotationTool.LineStrip or AnnotationTool.Highlighter) shape.Tag = tool;
        shape.Opacity = IsHighlighterTool(tool) ? Math.Min(0.45, opacity) : opacity;
        if (shape is Polyline poly) poly.Points.Add(point);
        Canvas.SetLeft(shape, point.X);
        Canvas.SetTop(shape, point.Y);
        return shape;
    }

    private void UpdateDrawingElement(FrameworkElement? element, Point start, Point end, AnnotationTool tool, double width)
    {
        if (element is Line line)
        {
            Canvas.SetLeft(line, 0); Canvas.SetTop(line, 0);
            line.X1 = start.X; line.Y1 = start.Y; line.X2 = end.X; line.Y2 = end.Y;
        }
        else if (element is System.Windows.Shapes.Path arrow && IsArrowTool(tool))
        {
            Canvas.SetLeft(arrow, 0); Canvas.SetTop(arrow, 0);
            SetArrowGeometry(arrow, new ArrowEndpoints(tool, start, end, width));
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
        if (tool is AnnotationTool.Arrow or AnnotationTool.DoubleArrow)
        {
            context.BeginFigure(start, false, false);
            context.LineTo(end, true, false);
            context.BeginFigure(upperHead, false, false);
            context.LineTo(end, true, false);
            context.LineTo(lowerHead, true, false);
            if (tool == AnnotationTool.DoubleArrow)
            {
                context.BeginFigure(new Point(start.X + ux * headLength + px * headHalfWidth, start.Y + uy * headLength + py * headHalfWidth), false, false);
                context.LineTo(start, true, false);
                context.LineTo(new Point(start.X + ux * headLength - px * headHalfWidth, start.Y + uy * headLength - py * headHalfWidth), true, false);
            }
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

    private FrameworkElement CreateFilteredRegion(PixelRect region, AnnotationTool tool)
    {
        region = region.ClampTo(_selection);
        if (region.IsEmpty)
            region = new PixelRect(Math.Clamp((int)_start.X, _selection.Left, _selection.Right - 1),
                Math.Clamp((int)_start.Y, _selection.Top, _selection.Bottom - 1), 1, 1);
        var image = new Image { Stretch = Stretch.Fill, Tag = new FilterSettings(tool, _filterStrength, EllipseFilter.IsChecked == true) };
        SetRect(image, region.X, region.Y, region.Width, region.Height);
        return image;
    }

    private void RefreshAnnotationFilters()
    {
        if (_selection.IsEmpty || !_annotations.Any(element => element is Image { Tag: FilterSettings })) return;

        // Sample each filter against the layers below it, including already refreshed filters.
        var layers = AnnotationCanvas.Children.OfType<FrameworkElement>()
            .Where(element => element.Visibility == Visibility.Visible).ToArray();
        var firstFilter = Array.FindIndex(layers, element => element is Image { Tag: FilterSettings });
        if (firstFilter < 0) return;
        try
        {
            for (var i = firstFilter; i < layers.Length; i++) layers[i].Visibility = Visibility.Hidden;
            for (var i = firstFilter; i < layers.Length; i++)
            {
                var layer = layers[i];
                if (layer is Image { Tag: FilterSettings filter } image) UpdateFilteredImage(image, filter);
                layer.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            for (var i = firstFilter; i < layers.Length; i++) layers[i].Visibility = Visibility.Visible;
        }
    }

    private void UpdateFilteredImage(Image image, FilterSettings filter)
    {
        var region = new PixelRect((int)Math.Round(GetLeft(image)), (int)Math.Round(GetTop(image)),
            (int)image.Width, (int)image.Height);
        var visible = region.ClampTo(_selection);
        var bounds = new Rect(0, 0, image.Width, image.Height);
        image.Clip = filter.Elliptical || filter.Tool == AnnotationTool.Magnify ? new EllipseGeometry(bounds) : null;
        if (visible.IsEmpty) { image.Source = null; return; }

        var sample = filter.Tool == AnnotationTool.Magnify
            ? new PixelRect(visible.X + visible.Width / 4, visible.Y + visible.Height / 4,
                Math.Max(1, visible.Width / 2), Math.Max(1, visible.Height / 2))
            : visible;
        var original = RenderSelection();
        var factorX = original.PixelWidth / (double)_selection.Width;
        var factorY = original.PixelHeight / (double)_selection.Height;
        var left = Math.Clamp((int)((sample.X - _selection.X) * factorX), 0, original.PixelWidth - 1);
        var top = Math.Clamp((int)((sample.Y - _selection.Y) * factorY), 0, original.PixelHeight - 1);
        var width = Math.Clamp((int)Math.Round(sample.Width * factorX), 1, original.PixelWidth - left);
        var height = Math.Clamp((int)Math.Round(sample.Height * factorY), 1, original.PixelHeight - top);
        var stride = width * 4;
        var pixels = new byte[stride * height];
        original.CopyPixels(new Int32Rect(left, top, width, height), pixels, stride, 0);
        if (filter.Tool == AnnotationTool.Blur) ApplyGaussianBlur(pixels, width, height, stride, Math.Max(1, filter.Strength / 2));
        else if (filter.Tool == AnnotationTool.Mosaic) ApplyMosaic(pixels, width, height, stride, filter.Strength);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        if (visible == region) image.Source = bitmap;
        else
        {
            var group = new DrawingGroup();
            using (var drawing = group.Open())
            {
                drawing.DrawRectangle(Brushes.Transparent, null, bounds);
                drawing.DrawImage(bitmap, new Rect(visible.X - region.X, visible.Y - region.Y, visible.Width, visible.Height));
            }
            group.Freeze();
            image.Source = new DrawingImage(group);
        }
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

    private void AddAnnotationCommand(FrameworkElement element)
    {
        if (AnnotationCanvas.Children.Contains(element)) AnnotationCanvas.Children.Remove(element);
        AddAnnotations([element]);
        if (IsResizableShape(element) || _preferences.SelectNewAnnotation) SelectAnnotations([element]);
    }

    private void AddAnnotations(FrameworkElement[] elements)
    {
        if (elements.Length == 0) return;
        Edit(() =>
        {
            foreach (var element in elements) { AnnotationCanvas.Children.Add(element); _annotations.Add(element); }
        }, () =>
        {
            foreach (var element in elements) { AnnotationCanvas.Children.Remove(element); _annotations.Remove(element); }
        });
    }

    private void EraseAt(Point point)
    {
        var element = FindAnnotationAt(point);
        if (element is null) return;
        DeleteAnnotation(element);
    }

    private void DeleteAnnotation(FrameworkElement element)
    {
        var index = _annotations.IndexOf(element);
        var childIndex = AnnotationCanvas.Children.IndexOf(element);
        Edit(() => { AnnotationCanvas.Children.Remove(element); _annotations.Remove(element); },
            () => { AnnotationCanvas.Children.Insert(childIndex, element); _annotations.Insert(index, element); });
    }

    private BitmapSource RenderSelection()
    {
        if (_selection.IsEmpty) throw new InvalidOperationException("尚未選取截圖範圍。");
        var rect = new Int32Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height);
        var original = _editingSticker ? ReplayedScreenshot.Source as BitmapSource : null;
        var width = original?.PixelWidth ?? _selection.Width;
        var height = original?.PixelHeight ?? _selection.Height;
        var bounds = new Rect(0, 0, width, height);
        var selectionBounds = new Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height);
        AnnotationCanvas.Measure(new Size(_frame.Bounds.Width, _frame.Bounds.Height));
        AnnotationCanvas.Arrange(new Rect(0, 0, _frame.Bounds.Width, _frame.Bounds.Height));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushClip(new RectangleGeometry(bounds));
            if (original is not null) drawing.DrawImage(original, bounds);
            else if (_board == CaptureBoard.White) drawing.DrawRectangle(Brushes.White, null, bounds);
            else if (_board == CaptureBoard.Desktop)
            {
                if (ReplayedScreenshot.Source is BitmapSource replayed)
                {
                    var replayBounds = new Rect(Canvas.GetLeft(ReplayedScreenshot) - _selection.X,
                        Canvas.GetTop(ReplayedScreenshot) - _selection.Y, ReplayedScreenshot.Width, ReplayedScreenshot.Height);
                    if (!replayBounds.Contains(bounds))
                    {
                        drawing.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds),
                            new RectangleGeometry(replayBounds)));
                        drawing.DrawImage(new CroppedBitmap(_bitmap, rect), bounds);
                        drawing.Pop();
                    }
                    drawing.DrawImage(replayed, replayBounds);
                }
                else drawing.DrawImage(new CroppedBitmap(_bitmap, rect), bounds);
            }
            var annotations = new VisualBrush(AnnotationCanvas)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = selectionBounds,
                ViewportUnits = BrushMappingMode.Absolute, Viewport = bounds,
                Stretch = Stretch.Fill
            };
            drawing.DrawRectangle(annotations, null, bounds);
            drawing.Pop();
        }
        var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze();
        return result;
    }

    private void CopyAndClose()
    {
        var copyFile = !CommandOutput && _preferences.CopyImageAsFile && _clipboard is ClipboardService;
        CompleteCapture(image =>
        {
            if (CommandOutput) return;
            var png = ClipboardService.EncodePng(image);
            if (copyFile && _clipboard is ClipboardService clipboard)
                clipboard.SetPng(png, CaptureOutput.QuickSave(image, _outputDirectory));
            else _clipboard.SetPng(png);
        }, saved: copyFile);
    }

    private void Save()
    {
        FinishTextEditor();
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            var dialog = new SaveFileDialog
            {
                InitialDirectory = _outputDirectory, Filter = "PNG 圖片|*.png|JPEG 圖片|*.jpg",
                FileName = $"MySnip-{DateTime.Now:yyyyMMdd-HHmmss}.png"
            };
            if (!_isSelectionCommitted) return;
            bool? confirmed;
            if (_preferences.HideCaptureForSaveDialog) ToggleNativeOverlay(false);
            try { confirmed = dialog.ShowDialog(this); }
            finally { if (_preferences.HideCaptureForSaveDialog) ToggleNativeOverlay(true); }
            if (confirmed != true) return;
            CompleteCapture(image => File.WriteAllBytes(dialog.FileName, BitmapFactory.Encode(image, System.IO.Path.GetExtension(dialog.FileName))), saved: true);
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
        if ((!_preferences.ShowMagnifier || _isSelectionCommitted) && !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var radius = _preferences.MagnifierSampleRadius;
        var side = radius * 2 + 1;
        var global = ToGlobal(ToPixel(point));
        var pixels = CapturedPixelSampler.SampleBgraSquare(_frame, global, radius);
        var source = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
        source.Freeze();
        MagnifierPixels.Source = source;

        var color = CapturedPixelSampler.Sample(_frame, global);
        MagnifierCoordinate.Text = $"({global.X}, {global.Y})";
        MagnifierColor.Text = _showHex ? color.ToHexText() : color.ToRgbText();

        var location = MagnifierPlacement.Place(ToPixel(point), new PixelSize((int)Magnifier.Width, (int)Magnifier.Height), DisplayAt(ToPixel(point)));
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
        CommitSelection(DisplayAt(ToPixel(_currentPoint)));
    }

    private void CommitSelection(PixelRect selection)
    {
        if (_editingSticker && !_stickerEditSelection.IsEmpty) return;
        ReplayedScreenshot.Source = null;
        _selection = selection.ClampTo(CanvasBounds);
        RefreshAnnotationFilters();
        _hoverCandidate = null;
        _isSelectionCommitted = !_selection.IsEmpty;
        Magnifier.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        QueueCommandOutput();
    }

    private void QueueCommandOutput()
    {
        if (!AutoCommandOutput || !_isSelectionCommitted) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && _isSelectionCommitted && CompletedCapture is null) CopyAndClose();
        });
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_dragMode == DragMode.MoveInlineText)
        {
            if (e.Key == Key.Escape) EndInlineTextMove(_start, cancel: true);
            e.Handled = true;
            return;
        }
        if (_textEditor?.IsKeyboardFocusWithin == true) return;
        if (_dragMode == DragMode.DrawText && e.Key == Key.Escape)
        {
            InputCanvas.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_dragMode == DragMode.ResizeAnnotation)
        {
            if (e.Key == Key.Escape) CancelAnnotationResize();
            e.Handled = true;
            return;
        }
        if (HandleCaptureShortcut(e)) { e.Handled = true; return; }
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _showHex = true;
            UpdateMagnifier(_currentPoint);
            return;
        }
        if (e.Key == Key.Escape) { DialogResult = false; return; }
        if (_editingSticker && e.Key == Key.Enter) { PinAndClose(); return; }
        if (e.Key == Key.Enter || (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CopyAndClose(); return; }
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Save(); return; }
        if (e.Key == Key.T && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { PinAndClose(); return; }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Undo_Click(sender, e); return; }
        if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Redo_Click(sender, e); return; }
        if (e.Key == Key.D1 && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { SetTool(_lastShapeTool); return; }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.None) { SetTool(AnnotationTool.Select); return; }
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.None) { SetTool(AnnotationTool.Eraser); return; }
        if (e.Key == Key.W) { SelectSuggestedWindow(); return; }
        if (!_editingSticker && _isSelectionCommitted && !_selection.IsEmpty && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            _selection = _selection.MoveBy(e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0,
                e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0, CanvasBounds);
            UpdateSelectionVisual(); e.Handled = true;
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt) UpdateMagnifier(_currentPoint);
        if (e.Key is not (Key.LeftShift or Key.RightShift)) return;
        _showHex = false;
        UpdateMagnifier(_currentPoint);
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } || !Enum.TryParse<AnnotationTool>(tag, out var tool)) return;

        if (!ReferenceEquals(sender, ShapeToolButton) && !ReferenceEquals(sender, LineToolButton) &&
            !ReferenceEquals(sender, HighlighterToolButton) && !ReferenceEquals(sender, FilterToolButton) &&
            !ReferenceEquals(sender, PenToolButton) && !ReferenceEquals(sender, TextToolButton) &&
            !ReferenceEquals(sender, EraserToolButton) && TryChangeAnnotationTool(tool)) return;
        if (ReferenceEquals(sender, ShapeToolButton)) tool = _lastShapeTool;
        else if (ReferenceEquals(sender, LineToolButton)) tool = _lastLineTool;
        else if (ReferenceEquals(sender, HighlighterToolButton)) tool = _lastHighlighterTool;
        else if (ReferenceEquals(sender, FilterToolButton)) tool = _lastFilterTool;

        SetTool(tool);
    }

    private void SetTool(AnnotationTool tool)
    {
        FinishTextEditor();
        if (_tool != tool) FinishLineStrip();
        SelectAnnotations([]);
        _tool = tool;
        RestoreToolColor();
        if (IsShapeTool(tool))
            _lastShapeTool = tool;
        if (IsLineTool(tool)) _lastLineTool = tool;
        if (IsHighlighterTool(tool)) _lastHighlighterTool = tool;
        if (IsFilterTool(tool)) _lastFilterTool = tool;
        UpdateAnnotationOutlines();
        UpdatePointerCursor(_currentPoint);
        UpdateToolUi();
    }

    private void ShapeFill_Click(object sender, RoutedEventArgs e)
    {
        var filled = ReferenceEquals(sender, FilledShapeOption);
        var shape = IsShapeTool(ContextTool) ? ContextTool : _lastShapeTool;
        var ellipse = shape is AnnotationTool.Ellipse or AnnotationTool.FilledEllipse;
        SetShapeTool((ellipse, filled) switch
        {
            (true, true) => AnnotationTool.FilledEllipse,
            (true, false) => AnnotationTool.Ellipse,
            (false, true) => AnnotationTool.FilledRectangle,
            _ => AnnotationTool.Rectangle
        });
    }

    private void ShapeGeometry_Click(object sender, RoutedEventArgs e)
    {
        var ellipse = ReferenceEquals(sender, EllipseOption);
        var shape = IsShapeTool(ContextTool) ? ContextTool : _lastShapeTool;
        var filled = shape is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse;
        SetShapeTool((ellipse, filled) switch
        {
            (true, true) => AnnotationTool.FilledEllipse,
            (true, false) => AnnotationTool.Ellipse,
            (false, true) => AnnotationTool.FilledRectangle,
            _ => AnnotationTool.Rectangle
        });
    }

    private void SetShapeTool(AnnotationTool tool)
    {
        if (!TryChangeAnnotationTool(tool)) SetTool(tool);
    }

    private void Style_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _updatingStyleControls) return;
        if (_textEditor is not null && _textBeingEdited is not null)
        {
            UpdateInlineTextStyle();
            return;
        }
        if (SelectedAnnotation() is { } element)
        {
            ChangeAnnotationStyle(element, sender);
            return;
        }
        _drawingWidth = WidthSlider.Value;
        _drawingOpacity = OpacitySlider.Value;
        _filterStrength = (int)Math.Round(FilterStrengthSlider.Value);
        UpdateStyleLabels();
    }

    private void TextStyle_Click(object sender, RoutedEventArgs e) => Style_Changed(sender, e);

    private void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        SetDrawingColor((Color)ColorConverter.ConvertFromString(color));
    }

    private void MoreColors_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(ContextColor.R, ContextColor.G, ContextColor.B)
        };
        var owner = new NativeWindowOwner(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        SetDrawingColor(Color.FromArgb(255, dialog.Color.R, dialog.Color.G, dialog.Color.B));
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
        var tool = ContextTool;
        ShapeToolButton.IsChecked = IsShapeTool(tool);
        LineToolButton.IsChecked = IsLineTool(tool);
        PenToolButton.IsChecked = tool == AnnotationTool.Pen;
        HighlighterToolButton.IsChecked = IsHighlighterTool(tool);
        FilterToolButton.IsChecked = IsFilterTool(tool);
        TextToolButton.IsChecked = tool == AnnotationTool.Text;
        EraserToolButton.IsChecked = tool == AnnotationTool.Eraser;
        OutlineShapeOption.IsChecked = tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse;
        FilledShapeOption.IsChecked = tool is AnnotationTool.FilledRectangle or AnnotationTool.FilledEllipse;
        RectangleOption.IsChecked = tool is AnnotationTool.Rectangle or AnnotationTool.FilledRectangle;
        EllipseOption.IsChecked = tool is AnnotationTool.Ellipse or AnnotationTool.FilledEllipse;
        RoundedOption.IsChecked = tool == AnnotationTool.RoundedRectangle;
        LineOption.IsChecked = tool == AnnotationTool.Line;
        ArrowOption.IsChecked = tool == AnnotationTool.Arrow;
        OutlineArrowOption.IsChecked = tool == AnnotationTool.OutlineArrow;
        FilledArrowOption.IsChecked = tool == AnnotationTool.FilledArrow;
        DoubleArrowOption.IsChecked = tool == AnnotationTool.DoubleArrow;
        LineStripOption.IsChecked = tool == AnnotationTool.LineStrip;
        HighlighterLineOption.IsChecked = tool == AnnotationTool.Highlighter;
        HighlighterRectangleOption.IsChecked = tool == AnnotationTool.HighlighterRectangle;
        HighlighterEllipseOption.IsChecked = tool == AnnotationTool.HighlighterEllipse;
        MosaicOption.IsChecked = tool == AnnotationTool.Mosaic;
        BlurOption.IsChecked = tool == AnnotationTool.Blur;
        MagnifyOption.IsChecked = tool == AnnotationTool.Magnify;

        ShapeOptions.Visibility = Show(IsShapeTool(tool));
        LineOptions.Visibility = Show(IsLineTool(tool));
        HighlighterOptions.Visibility = Show(IsHighlighterTool(tool));
        FilterOptions.Visibility = Show(IsFilterTool(tool));
        TextOptions.Visibility = Show(tool == AnnotationTool.Text);

        var supportsStroke = IsShapeTool(tool) || IsLineTool(tool) || tool == AnnotationTool.Pen || IsHighlighterTool(tool);
        var supportsColor = supportsStroke || tool is AnnotationTool.Text or AnnotationTool.Number;
        StrokeOptions.Visibility = Show(supportsStroke);
        ColorOptions.Visibility = Show(supportsColor);
        OpacityOptions.Visibility = Show(supportsColor);
        ContextToolbar.Visibility = Show(supportsColor || IsFilterTool(tool));
        UpdateColorPreview();

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsShapeTool(AnnotationTool tool) => tool is AnnotationTool.Rectangle or AnnotationTool.FilledRectangle or
        AnnotationTool.Ellipse or AnnotationTool.FilledEllipse or AnnotationTool.RoundedRectangle;

    private static bool IsArrowTool(AnnotationTool tool) => tool is AnnotationTool.Arrow or AnnotationTool.OutlineArrow or AnnotationTool.FilledArrow or AnnotationTool.DoubleArrow;

    private static bool IsLineTool(AnnotationTool tool) => tool is AnnotationTool.Line or AnnotationTool.LineStrip || IsArrowTool(tool);

    private static bool IsFilterTool(AnnotationTool tool) => tool is AnnotationTool.Mosaic or AnnotationTool.Blur or AnnotationTool.Magnify;

    private static bool IsHighlighterTool(AnnotationTool tool) =>
        tool is AnnotationTool.Highlighter or AnnotationTool.HighlighterRectangle or AnnotationTool.HighlighterEllipse;

    private void UpdateStyleLabels()
    {
        WidthValue.Text = $"{Math.Round(WidthSlider.Value):0}";
        OpacityValue.Text = $"{Math.Round(OpacitySlider.Value * 100):0}%";
        FilterStrengthValue.Text = Math.Round(FilterStrengthSlider.Value).ToString();
    }

    private void UpdateColorPreview()
    {
        if (CurrentColorPreview is null) return;
        var activeColor = ContextColor;
        CurrentColorPreview.Background = new SolidColorBrush(activeColor);
        foreach (var button in ColorPalette.Children.OfType<Button>())
        {
            var selected = button.Tag is string color &&
                string.Equals(color, activeColor.ToString(), StringComparison.OrdinalIgnoreCase);
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

    private void Undo_Click(object sender, RoutedEventArgs e) { FinishTextEditor(); _undo.Undo(); RefreshAnnotationEdits(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { FinishTextEditor(); _undo.Redo(); RefreshAnnotationEdits(); }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyAndClose();
    private void Save_Click(object sender, RoutedEventArgs e) => Save();
    private void Sticker_Click(object sender, RoutedEventArgs e) => PinAndClose();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private sealed class NativeWindowOwner(nint handle) : System.Windows.Forms.IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    private Point ClampPoint(Point point) => new(Math.Clamp(point.X, 0, _frame.Bounds.Width), Math.Clamp(point.Y, 0, _frame.Bounds.Height));
    private Point PointerPosition(MouseEventArgs e) => NativeMethods.GetCursorPos(out var cursor)
        ? ClampPoint(new Point(cursor.X - _frame.Bounds.X, cursor.Y - _frame.Bounds.Y))
        : ClampPoint(e.GetPosition(InputCanvas));
    private static bool CanDrawOutsideSelection(AnnotationTool tool) =>
        IsShapeTool(tool) || tool is AnnotationTool.Text or AnnotationTool.HighlighterRectangle or AnnotationTool.HighlighterEllipse;

    private Point ClampToSelectionIfDrawing(Point point) => _dragMode == DragMode.Draw && !CanDrawOutsideSelection(_tool)
        ? new Point(Math.Clamp(point.X, _selection.Left, _selection.Right), Math.Clamp(point.Y, _selection.Top, _selection.Bottom)) : point;
    private static PixelPoint ToPixel(Point point) => new((int)Math.Round(point.X), (int)Math.Round(point.Y));
    private PixelPoint ToGlobal(PixelPoint local) => new(local.X + _frame.Bounds.X, local.Y + _frame.Bounds.Y);
    private static double GetLeft(FrameworkElement element) => double.IsNaN(Canvas.GetLeft(element)) ? 0 : Canvas.GetLeft(element);
    private static double GetTop(FrameworkElement element) => double.IsNaN(Canvas.GetTop(element)) ? 0 : Canvas.GetTop(element);
}
