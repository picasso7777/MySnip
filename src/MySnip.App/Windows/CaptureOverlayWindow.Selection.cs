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
    private readonly List<Rectangle> _annotationOutlines = [];
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
        var selection = elements.ToArray();
        _selectedAnnotations.Clear();
        _selectedAnnotations.UnionWith(selection);
        UpdateAnnotationOutlines();
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

    private void UpdateAnnotationOutlines()
    {
        foreach (var outline in _annotationOutlines) MaskCanvas.Children.Remove(outline);
        _annotationOutlines.Clear();
        foreach (var element in _selectedAnnotations)
        {
            if (AnnotationBounds(element) is not { } bounds || bounds.IsEmpty) continue;
            bounds.Inflate(2, 2);
            var outline = new Rectangle { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1, StrokeDashArray = [3, 2] };
            SetRect(outline, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            MaskCanvas.Children.Add(outline);
            _annotationOutlines.Add(outline);
        }
    }

    private bool HandleAnnotationShortcut(KeyEventArgs e)
    {
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
        if (e.Key == Key.V && _annotationClipboard.Length > 0 && _isSelectionCommitted)
        {
            var clones = _annotationClipboard.Select(CloneAnnotation).OfType<FrameworkElement>().ToArray();
            foreach (var clone in clones) SetPosition(clone, new Point(GetLeft(clone) + 12, GetTop(clone) + 12));
            AddAnnotations(clones);
            SelectAnnotations(clones);
            return true;
        }
        return false;
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
