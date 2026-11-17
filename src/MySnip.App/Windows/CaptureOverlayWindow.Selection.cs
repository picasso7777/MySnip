using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MySnip.Core.Models;
using ShapePath = System.Windows.Shapes.Path;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private readonly HashSet<FrameworkElement> _selectedAnnotations = [];
    private static readonly Vector[] ShapeHandleDirections =
    [
        new(-1, -1), new(0, -1), new(1, -1), new(-1, 0),
        new(1, 0), new(-1, 1), new(0, 1), new(1, 1)
    ];
    private static readonly Cursor[] ShapeResizeCursors = [Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW];
    private readonly List<Rectangle> _annotationOutlines = [];
    private readonly Ellipse[] _annotationResizeHandles = new Ellipse[ShapeHandleDirections.Length];
    private (FrameworkElement Element, Rect OriginalBounds, Vector Handle, double FontSize)? _annotationResize;
    private readonly Dictionary<FrameworkElement, Point> _annotationOrigins = [];
    private FrameworkElement[] _annotationClipboard = [];
    private Rectangle? _marquee;
    private bool _freeSelect;
    private bool _cloneOnDrag;
    private bool _dragClones;

    private bool StartAnnotationSelection(Point start, FrameworkElement? hit)
    {
        var modifiers = Keyboard.Modifiers;
        if (hit is not null)
        {
            if (modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (!_selectedAnnotations.Add(hit)) _selectedAnnotations.Remove(hit);
                UpdateAnnotationOutlines();
                SyncAnnotationToolbar();
                _dragMode = DragMode.None;
                InputCanvas.ReleaseMouseCapture();
                return true;
            }
            if (!_selectedAnnotations.Contains(hit)) SelectAnnotations([hit]);
            _annotationOrigins.Clear();
            foreach (var element in _selectedAnnotations) _annotationOrigins[element] = new Point(GetLeft(element), GetTop(element));
            _cloneOnDrag = modifiers.HasFlag(ModifierKeys.Control);
            _dragClones = false;
            _dragMode = DragMode.MoveElement;
            return true;
        }
        if ((_freeSelect || modifiers.HasFlag(ModifierKeys.Shift)) && _selection.Contains(ToPixel(start)))
        {
            _marquee = new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(28, 39, 167, 231)),
                Stroke = Brushes.DeepSkyBlue, StrokeDashArray = [4, 2], StrokeThickness = 1 };
            MaskCanvas.Children.Add(_marquee);
            _dragMode = DragMode.SelectAnnotations;
            return true;
        }
        SelectAnnotations([]);
        return false;
    }

    private void UpdateAnnotationMove(Point current)
    {
        var delta = current - _start;
        if (_cloneOnDrag)
        {
            if (Math.Abs(delta.X) < 3 && Math.Abs(delta.Y) < 3) return;
            var clones = CloneSelected(0);
            _annotationOrigins.Clear();
            foreach (var clone in clones)
            {
                AnnotationCanvas.Children.Add(clone);
                _annotationOrigins[clone] = new Point(GetLeft(clone), GetTop(clone));
            }
            _selectedAnnotations.Clear();
            _selectedAnnotations.UnionWith(clones);
            _cloneOnDrag = false;
            _dragClones = true;
        }
        foreach (var (element, origin) in _annotationOrigins) SetPosition(element, origin + delta);
        UpdateAnnotationOutlines();
    }

    private void EndAnnotationMove(Point current)
    {
        UpdateAnnotationMove(current);
        if (_dragClones)
        {
            var clones = _annotationOrigins.Keys.ToArray();
            foreach (var clone in clones) AnnotationCanvas.Children.Remove(clone);
            AddAnnotations(clones);
            SelectAnnotations(clones);
        }
        else
        {
            var moves = _annotationOrigins.Select(pair =>
                (Element: pair.Key, From: pair.Value, To: new Point(GetLeft(pair.Key), GetTop(pair.Key))))
                .Where(move => move.From != move.To).ToArray();
            if (moves.Length > 0) Edit(
                () => { foreach (var move in moves) SetPosition(move.Element, move.To); },
                () => { foreach (var move in moves) SetPosition(move.Element, move.From); });
        }
        _annotationOrigins.Clear();
        _cloneOnDrag = _dragClones = false;
    }

    private void UpdateMarquee(Point end)
    {
        if (_marquee is not { } marquee) return;
        var region = new Rect(_start, end);
        SetRect(marquee, region.X, region.Y, region.Width, region.Height);
    }

    private void CommitMarquee(Point end)
    {
        if (_marquee is not { } marquee) return;
        MaskCanvas.Children.Remove(marquee);
        _marquee = null;
        var region = new Rect(_start, end);
        SelectAnnotations(_annotations.Where(element => AnnotationBounds(element) is { } bounds && region.IntersectsWith(bounds)));
    }

    private void SelectAnnotations(IEnumerable<FrameworkElement> elements)
    {
        var selection = elements.Where(_annotations.Contains).ToArray();
        _selectedAnnotations.Clear();
        _selectedAnnotations.UnionWith(selection);
        UpdateAnnotationOutlines();
        SyncAnnotationToolbar();
    }

    private Rect? AnnotationBounds(FrameworkElement element)
    {
        if (VisualTreeHelper.GetParent(element) != AnnotationCanvas) return null;
        var bounds = VisualTreeHelper.GetDescendantBounds(element);
        if (bounds.IsEmpty)
        {
            var width = element.ActualWidth > 0 ? element.ActualWidth : element.Width;
            var height = element.ActualHeight > 0 ? element.ActualHeight : element.Height;
            if (!double.IsFinite(width) || !double.IsFinite(height)) return null;
            bounds = new Rect(0, 0, width, height);
        }
        return element.TransformToAncestor(AnnotationCanvas).TransformBounds(bounds);
    }

    private static bool IsResizableShape(FrameworkElement element) => element is Rectangle or Ellipse;

    private Shape? SelectedResizableShape() =>
        _selectedAnnotations.Count == 1 && _selectedAnnotations.Single() is Shape shape &&
        IsResizableShape(shape) && _annotations.Contains(shape) && _tool != AnnotationTool.Eraser && _lineStrip is null
            ? shape : null;

    private static Rect ShapeBounds(Shape shape) =>
        new(GetLeft(shape), GetTop(shape), shape.Width, shape.Height);

    private static Vector RotateShapeOffset(FrameworkElement shape, Vector offset, bool inverse = false) =>
        shape.RenderTransform is RotateTransform rotation
            ? new RotateTransform(inverse ? -rotation.Angle : rotation.Angle).Value.Transform(offset)
            : offset;

    private static Point ShapeHandlePosition(FrameworkElement shape, Rect bounds, Vector direction)
    {
        var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        return center + RotateShapeOffset(shape, new Vector(direction.X * bounds.Width / 2, direction.Y * bounds.Height / 2));
    }

    private FrameworkElement? FindAnnotationAt(Point point, bool precise = false)
    {
        var inside = IsInsideCapture(point);
        var candidates = _annotations.AsEnumerable().Reverse().Where(element => element.IsVisible &&
            (inside || element is OutlinedAnnotationText || IsResizableShape(element)));
        foreach (var element in candidates)
        {
            if (HitsAnnotationStroke(element, point)) return element;
        }
        if (precise) return null;
        foreach (var element in candidates)
        {
            if (AnnotationBounds(element) is not { } bounds) continue;
            bounds.Inflate(4, 4);
            if (bounds.Contains(point)) return element;
        }
        return null;
    }

    private bool HitsAnnotationStroke(FrameworkElement element, Point point)
    {
        if (AnnotationBounds(element) is not { } bounds) return false;
        bounds.Inflate(5, 5);
        if (!bounds.Contains(point)) return false;
        var transform = element.TransformToAncestor(AnnotationCanvas).Inverse;
        if (transform is null || !transform.TryTransform(point, out var local)) return false;
        if (element is Image { Tag: FilterSettings filter } image)
        {
            var area = new Rect(0, 0, image.Width, image.Height);
            Geometry geometry = filter.Elliptical || filter.Tool == AnnotationTool.Magnify
                ? new EllipseGeometry(area) : new RectangleGeometry(area);
            return geometry.StrokeContains(new Pen(Brushes.Black, 8), local);
        }
        if (element is not Shape shape) return true;
        var rendered = shape.RenderedGeometry;
        if (rendered.IsEmpty()) return false;
        var pen = new Pen(Brushes.Black, (shape.Stroke is null ? 0 : shape.StrokeThickness) + 8)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        return rendered.StrokeContains(pen, local) ||
            shape is ShapePath { Tag: ArrowEndpoints { Tool: AnnotationTool.FilledArrow } } && rendered.FillContains(local);
    }

    private (FrameworkElement Element, Vector Direction)? HitAnnotationHandle(Point point)
    {
        if (!_isSelectionCommitted || _tool == AnnotationTool.Eraser || ResizeTarget is not { } target) return null;
        var bounds = AnnotationResizeBounds(_textEditor ?? target);
        var nearestDistance = double.PositiveInfinity;
        Vector? nearest = null;
        foreach (var direction in ShapeHandleDirections)
        {
            if (target is OutlinedAnnotationText && !IsTextCorner(direction)) continue;
            var position = ShapeHandlePosition(target, bounds, direction);
            var distance = (position - point).LengthSquared;
            var radius = target is OutlinedAnnotationText ? TextHandleRadius + 1 : 6;
            if (distance > radius * radius || distance >= nearestDistance) continue;
            nearest = direction;
            nearestDistance = distance;
        }
        return nearest is { } handle ? (target, handle) : null;
    }

    private bool IsInsideCapture(Point point) =>
        point.X >= _selection.Left && point.X <= _selection.Right &&
        point.Y >= _selection.Top && point.Y <= _selection.Bottom;

    private static Cursor AnnotationResizeCursor(FrameworkElement shape, Vector direction)
    {
        var offset = RotateShapeOffset(shape, direction);
        var octant = (int)Math.Round(Math.Atan2(offset.Y, offset.X) * 4 / Math.PI);
        return ShapeResizeCursors[(octant + 4) % 4];
    }

    private void UpdateAnnotationResize(Point point)
    {
        if (_annotationResize is not { } resize) return;
        var (shape, bounds, handle, fontSize) = resize;
        var delta = RotateShapeOffset(shape, point - _start, inverse: true);
        if (shape is OutlinedAnnotationText text)
        {
            UpdateTextCornerResize(text, bounds, handle, delta, fontSize);
            UpdateAnnotationOutlines();
            return;
        }
        var left = handle.X < 0 ? Math.Min(bounds.Left + delta.X, bounds.Right - 1) : bounds.Left;
        var right = handle.X > 0 ? Math.Max(bounds.Right + delta.X, bounds.Left + 1) : bounds.Right;
        var top = handle.Y < 0 ? Math.Min(bounds.Top + delta.Y, bounds.Bottom - 1) : bounds.Top;
        var bottom = handle.Y > 0 ? Math.Max(bounds.Bottom + delta.Y, bounds.Top + 1) : bounds.Bottom;

        // Keep the opposite handle anchored when the shape is rotated.
        var shift = new Vector((left + right - bounds.Left - bounds.Right) / 2,
                               (top + bottom - bounds.Top - bounds.Bottom) / 2);
        var offset = RotateShapeOffset(shape, shift) - shift;
        SetRect(shape, left + offset.X, top + offset.Y, right - left, bottom - top);
        UpdateAnnotationOutlines();
    }

    private void EndAnnotationResize(Point point)
    {
        if (_annotationResize is not { } resize) return;
        UpdateAnnotationResize(point);
        var (shape, original, _, fontSize) = resize;
        _annotationResize = null;
        if (shape is OutlinedAnnotationText text)
        {
            EditAnnotationValue((original, fontSize), (AnnotationResizeBounds(text), text.FontSize),
                value => SetTextGeometry(text, value.Item1, value.Item2));
            ResumeTextEditor(text);
        }
        else CommitAnnotationResize((Shape)shape, original, ShapeBounds((Shape)shape));
    }

    private void ResizeAnnotationWithWheel(Shape shape, int delta)
    {
        var original = ShapeBounds(shape);
        var minimum = Math.Min(1, 1 / Math.Min(original.Width, original.Height));
        var maximum = Math.Max(1, Math.Min(CanvasBounds.Width / original.Width, CanvasBounds.Height / original.Height));
        var scale = Math.Clamp(Math.Pow(1.1, delta / 120d), minimum, maximum);
        if (scale == 1) return;

        var width = original.Width * scale;
        var height = original.Height * scale;
        var resized = new Rect(original.X + (original.Width - width) / 2,
            original.Y + (original.Height - height) / 2, width, height);
        CommitAnnotationResize(shape, original, resized);
    }

    private void CommitAnnotationResize(Shape shape, Rect original, Rect resized)
    {
        EditAnnotationValue(original, resized,
            bounds => SetRect(shape, bounds.X, bounds.Y, bounds.Width, bounds.Height));
    }

    private void CancelAnnotationResize()
    {
        if (_annotationResize is not { } resize) return;
        var (shape, original, _, fontSize) = resize;
        _annotationResize = null;
        _dragMode = DragMode.None;
        if (shape is OutlinedAnnotationText text) SetTextGeometry(text, original, fontSize);
        else SetRect(shape, original.X, original.Y, original.Width, original.Height);
        InputCanvas.ReleaseMouseCapture();
        if (shape is OutlinedAnnotationText edited) ResumeTextEditor(edited);
        UpdateAnnotationOutlines();
        UpdatePointerCursor(_currentPoint);
    }

    private void UpdateAnnotationOutlines()
    {
        foreach (var outline in _annotationOutlines) MaskCanvas.Children.Remove(outline);
        _annotationOutlines.Clear();
        TextChromeCanvas.Children.Clear();
        var selectedShape = _isSelectionCommitted && _tool != AnnotationTool.Eraser ? ResizeTarget : null;
        var targetBounds = selectedShape is null ? Rect.Empty : AnnotationResizeBounds(_textEditor ?? selectedShape);
        for (var i = 0; i < _annotationResizeHandles.Length; i++)
        {
            var handle = _annotationResizeHandles[i];
            handle.Visibility = Visibility.Collapsed;
            if (selectedShape is null or OutlinedAnnotationText) continue;
            var direction = ShapeHandleDirections[i];
            var position = ShapeHandlePosition(selectedShape, targetBounds, direction);
            SetPosition(handle, position - new Vector(3.5, 3.5));
            handle.Visibility = Visibility.Visible;
        }
        if (selectedShape is OutlinedAnnotationText textBox) DrawTextChrome(textBox, targetBounds);
        foreach (var element in _selectedAnnotations)
        {
            if (element == selectedShape || AnnotationBounds(element) is not { } bounds || bounds.IsEmpty) continue;
            bounds.Inflate(2, 2);
            var outline = new Rectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1, StrokeDashArray = [3, 2] };
            SetRect(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            MaskCanvas.Children.Add(outline);
            _annotationOutlines.Add(outline);
        }
    }

    private bool HandleAnnotationShortcut(KeyEventArgs e)
    {
        if (_selectedAnnotations.Count > 0 && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            var amount = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            var delta = new Vector(e.Key == Key.Left ? -amount : e.Key == Key.Right ? amount : 0,
                e.Key == Key.Up ? -amount : e.Key == Key.Down ? amount : 0);
            var origins = _selectedAnnotations.ToDictionary(element => element, element => new Point(GetLeft(element), GetTop(element)));
            Edit(() => { foreach (var (element, origin) in origins) SetPosition(element, origin + delta); },
                () => { foreach (var (element, origin) in origins) SetPosition(element, origin); });
            return true;
        }
        if (e.Key == Key.Delete && _selectedAnnotations.Any(_annotations.Contains))
        {
            DeleteSelectedAnnotations();
            return true;
        }
        if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return false;
        if (e.Key == Key.C && _selectedAnnotations.Any(_annotations.Contains))
        {
            _annotationClipboard = CloneSelected(0);
            return true;
        }
        if (e.Key != Key.V || _annotationClipboard.Length == 0 || !_isSelectionCommitted) return false;
        PasteAnnotations();
        return true;
    }

    private void PasteAnnotations()
    {
        if (_annotationClipboard.Length == 0 || !_isSelectionCommitted) return;
        var clones = _annotationClipboard.Select(CloneAnnotation).OfType<FrameworkElement>().ToArray();
        foreach (var clone in clones) SetPosition(clone, new Point(GetLeft(clone) + 12, GetTop(clone) + 12));
        AddAnnotations(clones);
        SelectAnnotations(clones);
    }

    private void DeleteSelectedAnnotations()
    {
        var removed = _selectedAnnotations.Where(_annotations.Contains)
            .Select(element => (Element: element, Order: _annotations.IndexOf(element), Child: AnnotationCanvas.Children.IndexOf(element)))
            .OrderBy(entry => entry.Order).ToArray();
        if (removed.Length == 0) return;
        Edit(() =>
        {
            foreach (var item in removed) { AnnotationCanvas.Children.Remove(item.Element); _annotations.Remove(item.Element); }
        }, () =>
        {
            foreach (var item in removed)
            {
                AnnotationCanvas.Children.Insert(item.Child, item.Element);
                _annotations.Insert(item.Order, item.Element);
            }
        });
        SelectAnnotations([]);
    }

    private FrameworkElement[] CloneSelected(double offset)
    {
        var clones = _annotations.Where(_selectedAnnotations.Contains).Select(CloneAnnotation).OfType<FrameworkElement>().ToArray();
        foreach (var clone in clones) SetPosition(clone, new Point(GetLeft(clone) + offset, GetTop(clone) + offset));
        return clones;
    }

    private static FrameworkElement? CloneAnnotation(FrameworkElement original)
    {
        FrameworkElement? copy = original switch
        {
            Rectangle r => new Rectangle { RadiusX = r.RadiusX, RadiusY = r.RadiusY },
            Ellipse => new Ellipse(),
            Line l => new Line { X1 = l.X1, X2 = l.X2, Y1 = l.Y1, Y2 = l.Y2 },
            Polyline p => new Polyline { Points = p.Points.Clone() },
            ShapePath p => new ShapePath { Data = p.Data?.CloneCurrentValue() },
            OutlinedAnnotationText t => new OutlinedAnnotationText
            {
                Text = t.Text, FontFamily = t.FontFamily, FontSize = t.FontSize, FontWeight = t.FontWeight, FontStyle = t.FontStyle,
                Foreground = t.Foreground, Background = t.Background, Outline = t.Outline, OutlineWidth = t.OutlineWidth
            },
            TextBlock t => CopyText(t),
            Border b when b.Child is TextBlock t => new Border { Background = b.Background, CornerRadius = b.CornerRadius, Child = CopyText(t) },
            Image i => new Image { Source = i.Source, Stretch = i.Stretch },
            _ => null
        };
        if (copy is null) return null;
        if (copy is Shape shape && original is Shape source)
        {
            shape.Fill = source.Fill;
            shape.Stroke = source.Stroke;
            shape.StrokeThickness = source.StrokeThickness;
            shape.StrokeDashArray = source.StrokeDashArray?.CloneCurrentValue();
            shape.StrokeStartLineCap = source.StrokeStartLineCap;
            shape.StrokeEndLineCap = source.StrokeEndLineCap;
            shape.StrokeLineJoin = source.StrokeLineJoin;
        }
        copy.Width = original.Width;
        copy.Height = original.Height;
        copy.Opacity = original.Opacity;
        copy.Tag = original.Tag;
        copy.Clip = original.Clip?.CloneCurrentValue();
        copy.RenderTransform = original.RenderTransform.CloneCurrentValue();
        copy.RenderTransformOrigin = original.RenderTransformOrigin;
        SetPosition(copy, new Point(GetLeft(original), GetTop(original)));
        return copy;
    }

    private static TextBlock CopyText(TextBlock source)
    {
        var result = new TextBlock();
        result.Text = source.Text;
        result.FontFamily = source.FontFamily;
        result.FontSize = source.FontSize;
        result.FontWeight = source.FontWeight;
        result.FontStyle = source.FontStyle;
        result.Foreground = source.Foreground;
        result.Background = source.Background;
        result.VerticalAlignment = source.VerticalAlignment;
        result.HorizontalAlignment = source.HorizontalAlignment;
        return result;
    }

    private static void SetPosition(UIElement element, Point point)
    {
        Canvas.SetLeft(element, point.X);
        Canvas.SetTop(element, point.Y);
    }
}
