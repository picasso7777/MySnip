using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MySnip.Core.Models;
using ShapePath = System.Windows.Shapes.Path;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private sealed record ArrowEndpoints(AnnotationTool Tool, Point Start, Point End, double Width);
    private sealed record FilterSettings(AnnotationTool Tool, int Strength, bool Elliptical);
    private sealed record ToolbarStyle(double Width, double Opacity, int Strength, bool Dashed, bool Elliptical,
        FontFamily Font, double FontSize, bool Bold, bool Italic, bool TextBackground);

    private ToolbarStyle? _draftToolbarStyle;
    private ComboBoxItem? _customFontSize;

    private FrameworkElement? SelectedAnnotation() =>
        _selectedAnnotations.Count == 1 ? _selectedAnnotations.Single() : null;

    private static AnnotationTool AnnotationKind(FrameworkElement element) => element switch
    {
        Image { Tag: FilterSettings filter } => filter.Tool,
        ShapePath { Tag: ArrowEndpoints arrow } => arrow.Tool,
        Rectangle { RadiusX: > 0 } => AnnotationTool.RoundedRectangle,
        Rectangle { Stroke: null } => AnnotationTool.HighlighterRectangle,
        Rectangle { Fill: not null } => AnnotationTool.FilledRectangle,
        Rectangle => AnnotationTool.Rectangle,
        Ellipse { Stroke: null } => AnnotationTool.HighlighterEllipse,
        Ellipse { Fill: not null } => AnnotationTool.FilledEllipse,
        Ellipse => AnnotationTool.Ellipse,
        Line { Tag: AnnotationTool.Highlighter } => AnnotationTool.Highlighter,
        Line => AnnotationTool.Line,
        Polyline { Tag: AnnotationTool.LineStrip } => AnnotationTool.LineStrip,
        Polyline => AnnotationTool.Pen,
        OutlinedAnnotationText => AnnotationTool.Text,
        Border { Tag: int } => AnnotationTool.Number,
        _ => AnnotationTool.Select
    };

    private AnnotationTool ContextTool => SelectedAnnotation() is { } element ? AnnotationKind(element) : _tool;

    private static Color AnnotationColor(FrameworkElement element, Color fallback)
    {
        var brush = element switch
        {
            Shape shape => shape.Stroke ?? shape.Fill,
            OutlinedAnnotationText text => text.Foreground,
            Border { Tag: int } number => number.Background,
            _ => null
        };
        return brush is SolidColorBrush color ? color.Color : fallback;
    }

    private Color ContextColor => SelectedAnnotation() is { } element ? AnnotationColor(element, _drawingColor) : _drawingColor;

    private ToolbarStyle ReadToolbarStyle() => new(WidthSlider.Value, OpacitySlider.Value, (int)Math.Round(FilterStrengthSlider.Value),
        DashedStroke.IsChecked == true, EllipseFilter.IsChecked == true,
        FontFamilyPicker.SelectedItem as FontFamily ?? new FontFamily("Microsoft JhengHei UI"), SelectedFontSize(),
        BoldToggle.IsChecked == true, ItalicToggle.IsChecked == true, TextBackground.IsChecked == true);

    private void SyncAnnotationToolbar()
    {
        if (!IsInitialized || _updatingStyleControls) return;
        var style = _draftToolbarStyle;
        var selected = _textBeingEdited ?? SelectedAnnotation();
        if (selected is not null)
        {
            _draftToolbarStyle ??= ReadToolbarStyle();
            style = _draftToolbarStyle with { Opacity = selected.Opacity };
            if (selected is Shape shape)
                style = style with
                {
                    Width = shape.Tag is ArrowEndpoints arrow ? arrow.Width :
                        AnnotationKind(shape) == AnnotationTool.Highlighter ? shape.StrokeThickness / 3 : shape.StrokeThickness,
                    Dashed = shape.StrokeDashArray is { Count: > 0 }
                };
            if (selected is Image { Tag: FilterSettings filter })
                style = style with { Strength = filter.Strength, Elliptical = filter.Elliptical };
            if (selected is OutlinedAnnotationText text)
                style = style with { Font = text.FontFamily, FontSize = text.FontSize,
                    Bold = text.FontWeight == FontWeights.Bold, Italic = text.FontStyle == FontStyles.Italic,
                    TextBackground = text.Background == Brushes.White };
        }
        else _draftToolbarStyle = null;

        if (style is not null)
        {
            _updatingStyleControls = true;
            WidthSlider.Value = style.Width;
            OpacitySlider.Value = style.Opacity;
            FilterStrengthSlider.Value = style.Strength;
            DashedStroke.IsChecked = style.Dashed;
            EllipseFilter.IsChecked = style.Elliptical;
            var font = FontFamilyPicker.Items.OfType<FontFamily>().FirstOrDefault(item => item.Source == style.Font.Source);
            if (font is null) { font = style.Font; FontFamilyPicker.Items.Add(font); }
            FontFamilyPicker.SelectedItem = font;
            SelectToolbarFontSize(style.FontSize);
            BoldToggle.IsChecked = style.Bold;
            ItalicToggle.IsChecked = style.Italic;
            TextBackground.IsChecked = style.TextBackground;
            _updatingStyleControls = false;
        }
        UpdateStyleLabels();
        UpdateToolUi();
    }

    private void SelectToolbarFontSize(double fontSize)
    {
        if (_customFontSize is not null) FontSizePicker.Items.Remove(_customFontSize);
        _customFontSize = null;
        var sizeItem = FontSizePicker.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => double.TryParse(item.Content?.ToString(), out var size) && size == fontSize);
        if (sizeItem is null)
        {
            sizeItem = _customFontSize = new ComboBoxItem { Content = fontSize.ToString("0.##") };
            FontSizePicker.Items.Add(sizeItem);
        }
        FontSizePicker.SelectedItem = sizeItem;
    }

    private void EditAnnotationValue<T>(T before, T after, Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(before, after)) return;
        Edit(() => setter(after), () => setter(before));
    }

    private void ChangeAnnotationColor(FrameworkElement element, Color color)
    {
        if (AnnotationColor(element, color) == color) return;
        var brush = new SolidColorBrush(color);
        switch (element)
        {
            case Shape shape:
                var before = (shape.Stroke, shape.Fill);
                var after = (shape.Stroke is not null ? brush : null,
                    shape.Fill is not null && shape.Fill != Brushes.Transparent ? brush : shape.Fill);
                EditAnnotationValue<(Brush? Stroke, Brush? Fill)>(before, after, value => (shape.Stroke, shape.Fill) = value);
                break;
            case OutlinedAnnotationText text:
                EditAnnotationValue<Brush>(text.Foreground, brush, value => text.Foreground = value);
                break;
            case Border { Tag: int } number:
                EditAnnotationValue<Brush>(number.Background, brush, value => number.Background = value);
                break;
        }
    }

    private void ApplyDrawingStyle(Shape shape)
    {
        var before = (shape.Stroke, shape.Fill, shape.StrokeThickness, shape.Opacity, shape.Tag);
        var kind = AnnotationKind(shape);
        var brush = new SolidColorBrush(_drawingColor);
        var width = _drawingWidth;
        var opacity = IsHighlighterTool(kind) ? Math.Min(0.45, _drawingOpacity) : _drawingOpacity;
        Edit(() =>
        {
            shape.Stroke = brush;
            if (shape.Fill is not null && shape.Fill != Brushes.Transparent) shape.Fill = brush;
            if (shape is ShapePath { Tag: ArrowEndpoints arrow } path) SetArrowGeometry(path, arrow with { Width = width });
            else shape.StrokeThickness = StrokeWidth(kind, width);
            shape.Opacity = opacity;
        }, () =>
        {
            if (shape is ShapePath path && before.Tag is ArrowEndpoints arrow) SetArrowGeometry(path, arrow);
            (shape.Stroke, shape.Fill, shape.StrokeThickness, shape.Opacity) =
                (before.Stroke, before.Fill, before.StrokeThickness, before.Opacity);
        });
    }

    private void ChangeAnnotationStyle(FrameworkElement element, object sender)
    {
        var style = ReadToolbarStyle();
        if (ReferenceEquals(sender, OpacitySlider))
        {
            EditAnnotationValue(element.Opacity, style.Opacity, value => element.Opacity = value);
        }
        else if (element is Shape shape)
        {
            if (ReferenceEquals(sender, DashedStroke))
                EditAnnotationValue(shape.StrokeDashArray is { Count: > 0 }, style.Dashed,
                    value => shape.StrokeDashArray = value ? [3, 2] : null);
            else if (ReferenceEquals(sender, WidthSlider))
            {
                if (shape is ShapePath { Tag: ArrowEndpoints arrow } path)
                    EditAnnotationValue(arrow.Width, style.Width, width => SetArrowGeometry(path, arrow with { Width = width }));
                else
                    EditAnnotationValue(shape.StrokeThickness, StrokeWidth(AnnotationKind(shape), style.Width),
                        value => shape.StrokeThickness = value);
            }
        }
        else if (element is Image { Tag: FilterSettings filter } image)
        {
            var updated = ReferenceEquals(sender, FilterStrengthSlider) ? filter with { Strength = style.Strength }
                : ReferenceEquals(sender, EllipseFilter) ? filter with { Elliptical = style.Elliptical } : filter;
            EditAnnotationValue(filter, updated, value => image.Tag = value);
        }
        else if (element is OutlinedAnnotationText text)
        {
            if (ReferenceEquals(sender, FontFamilyPicker))
                EditAnnotationValue(text.FontFamily, style.Font, value => text.FontFamily = value);
            else if (ReferenceEquals(sender, FontSizePicker))
                EditAnnotationValue((text.FontSize, text.Height), (style.FontSize, text.Height), value =>
                {
                    text.FontSize = value.Item1;
                    text.Height = Math.Max(value.Item2, text.HeightForText(text.Text, text.Width));
                });
            else if (ReferenceEquals(sender, BoldToggle))
                EditAnnotationValue(text.FontWeight, style.Bold ? FontWeights.Bold : FontWeights.Normal, value => text.FontWeight = value);
            else if (ReferenceEquals(sender, ItalicToggle))
                EditAnnotationValue(text.FontStyle, style.Italic ? FontStyles.Italic : FontStyles.Normal, value => text.FontStyle = value);
            else if (ReferenceEquals(sender, TextBackground))
                EditAnnotationValue<Brush>(text.Background, style.TextBackground ? Brushes.White : Brushes.Transparent, value => text.Background = value);
        }
    }

    private void SetArrowGeometry(ShapePath path, ArrowEndpoints arrow)
    {
        path.Tag = arrow;
        path.Data = CreateArrowGeometry(arrow.Start, arrow.End, arrow.Tool, arrow.Width);
        path.StrokeThickness = StrokeWidth(arrow.Tool, arrow.Width);
    }

    private static double StrokeWidth(AnnotationTool tool, double width) => tool switch
    {
        AnnotationTool.FilledArrow => 0.5,
        AnnotationTool.OutlineArrow => Math.Max(1, width / 2),
        AnnotationTool.Highlighter => Math.Max(10, width * 3),
        _ => width
    };

    private bool TryChangeAnnotationTool(AnnotationTool tool)
    {
        if (SelectedAnnotation() is not { } element) return false;
        var current = AnnotationKind(element);
        if (current == tool) { UpdateToolUi(); return true; }
        if (element is Image { Tag: FilterSettings filter } image && IsFilterTool(tool))
        {
            EditAnnotationValue(filter, filter with { Tool = tool }, value => image.Tag = value);
            return true;
        }
        if (element is not Shape shape ||
            !(IsShapeTool(tool) && IsShapeTool(current) || IsLineTool(tool) && IsLineTool(current) ||
              IsHighlighterTool(tool) && IsHighlighterTool(current))) return false;

        var style = ReadToolbarStyle();
        var replacement = CreateDrawingElement(tool, new Point(), AnnotationColor(shape, _drawingColor),
            style.Width, style.Opacity, style.Dashed);
        var (start, end) = shape switch
        {
            Line line => (new Point(line.X1, line.Y1), new Point(line.X2, line.Y2)),
            ShapePath { Tag: ArrowEndpoints arrow } => (arrow.Start, arrow.End),
            Polyline { Points.Count: > 0 } polyline => (polyline.Points[0], polyline.Points[^1]),
            _ => (new Point(), new Point(shape.Width, shape.Height))
        };
        if (replacement is Polyline strip) strip.Points.Clear();
        UpdateDrawingElement(replacement, start, end, tool, style.Width);
        if (replacement is Polyline converted) converted.Points.Insert(0, start);
        if (IsResizableShape(shape) && IsResizableShape(replacement))
            SetRect(replacement, GetLeft(shape), GetTop(shape), shape.Width, shape.Height);
        else
            SetPosition(replacement, new Point(GetLeft(shape) + GetLeft(replacement), GetTop(shape) + GetTop(replacement)));
        replacement.Opacity = shape.Opacity;
        replacement.RenderTransform = shape.RenderTransform;
        replacement.RenderTransformOrigin = shape.RenderTransformOrigin;
        ReplaceAnnotation(shape, replacement);
        return true;
    }

    private void ReplaceAnnotation(FrameworkElement original, FrameworkElement replacement)
    {
        var index = _annotations.IndexOf(original);
        var childIndex = AnnotationCanvas.Children.IndexOf(original);
        Edit(() => Swap(original, replacement), () => Swap(replacement, original));

        void Swap(FrameworkElement from, FrameworkElement to)
        {
            AnnotationCanvas.Children.Remove(from);
            AnnotationCanvas.Children.Insert(childIndex, to);
            _annotations[index] = to;
            if (_selectedAnnotations.Remove(from)) _selectedAnnotations.Add(to);
        }
    }
}
