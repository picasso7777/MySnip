using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Globalization;
using MySnip.Core.Commands;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private Polyline? _lineStrip;
    private bool _roundEraser;
    private double _outlineWidth;
    private Color _outlineColor = Colors.Black;

    private void TextOutline_Click(object sender, RoutedEventArgs e)
    {
        FinishTextEditor();
        var selected = SelectedAnnotation() as OutlinedAnnotationText;
        var currentColor = selected?.Outline is SolidColorBrush brush ? brush.Color : _outlineColor;
        var currentWidth = selected?.OutlineWidth ?? _outlineWidth;
        var value = InputDialog.Show(this, "文字描邊", "輸入 #AARRGGBB 色碼及粗細，中間用空格分隔。粗細 0 會關閉描邊。",
            $"{currentColor} {currentWidth.ToString(CultureInfo.InvariantCulture)}", text => ParseOutline(text, out _, out _));
        if (value is null || !ParseOutline(value, out var color, out var width)) return;
        if (selected is not null)
        {
            if (color != currentColor || width != currentWidth)
                EditAnnotationValue<(Brush Brush, double Width)>((selected.Outline, currentWidth),
                    (new SolidColorBrush(color), width), style => selected.SetOutline(style.Brush, style.Width));
        }
        else { _outlineColor = color; _outlineWidth = width; }
    }

    private static bool ParseOutline(string value, out Color color, out double width)
    {
        color = Colors.Black;
        width = 0;
        var fields = value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 || !AdvancedPreferences.IsColor(fields[0]) ||
            !double.TryParse(fields[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out width) || width is < 0 or > 12) return false;
        color = (Color)ColorConverter.ConvertFromString(fields[0]);
        return true;
    }

    private void EraseEllipse(Rect bounds)
    {
        if (bounds.Width < 2 || bounds.Height < 2) { EraseAt(bounds.TopLeft); return; }
        var ellipse = new EllipseGeometry(bounds);
        SelectAnnotations(_annotations.Where(element => AnnotationBounds(element) is { } area &&
            ellipse.FillContainsWithDetail(new RectangleGeometry(area)) != IntersectionDetail.Empty));
        DeleteSelectedAnnotations();
    }

    private void AddNumber(Point point)
    {
        var number = _annotations.Select(element => element.Tag is int value ? value : 0).DefaultIfEmpty().Max() + 1;
        var marker = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(_drawingColor), Opacity = _drawingOpacity, Tag = number,
            Child = new TextBlock { Text = number.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold,
                FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        Canvas.SetLeft(marker, point.X - marker.Width / 2);
        Canvas.SetTop(marker, point.Y - marker.Height / 2);
        AddAnnotationCommand(marker);
    }

    private void AddLineStripPoint(Point point, bool finish)
    {
        if (_lineStrip is null)
        {
            _lineStrip = new Polyline
            {
                Tag = AnnotationTool.LineStrip,
                Stroke = new SolidColorBrush(_drawingColor), StrokeThickness = _drawingWidth,
                StrokeDashArray = DashedStroke.IsChecked == true ? [3, 2] : null,
                StrokeLineJoin = PenLineJoin.Round, Opacity = _drawingOpacity
            };
            AnnotationCanvas.Children.Add(_lineStrip);
        }
        if (_lineStrip.Points.Count == 0 || _lineStrip.Points[^1] != point) _lineStrip.Points.Add(point);
        if (finish) FinishLineStrip();
    }

    private void FinishLineStrip()
    {
        if (_lineStrip is not { } line) return;
        _lineStrip = null;
        if (line.Points.Count > 1) AddAnnotationCommand(line);
        else AnnotationCanvas.Children.Remove(line);
    }

    private void ShowAnnotationOptions(FrameworkElement element)
    {
        if (!_selectedAnnotations.Contains(element)) SelectAnnotations([element]);
        var menu = new ContextMenu { PlacementTarget = InputCanvas, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        if (element is OutlinedAnnotationText text)
            Add("修改文字…", () => EditAnnotationText(text));
        Add("貼上標註（Ctrl+Shift+V）", PasteAnnotations);
        if (element is Shape { Stroke: not null } shape)
            Add("套用目前畫筆樣式", () => ApplyDrawingStyle(shape));
        if (element is not Image)
        {
            Add("向左旋轉 15°", () => Rotate(-15));
            Add("向右旋轉 15°", () => Rotate(15));
            Add("輸入旋轉角度…", () =>
            {
                var value = InputDialog.Show(this, "旋轉標註", "輸入要增加的角度（可為負數）", "45",
                    text => double.TryParse(text, out var degrees) && double.IsFinite(degrees) && Math.Abs(degrees) <= 3600);
                if (value is not null && double.TryParse(value, out var degrees)) Rotate(degrees);
            });
        }
        Add("複製標註（Ctrl+Shift+C）", () => _annotationClipboard = CloneSelected(0));
        Add("建立副本", () =>
        {
            var clones = CloneSelected(12);
            AddAnnotations(clones);
            SelectAnnotations(clones);
        });
        Add("刪除選取的標註", DeleteSelectedAnnotations);
        menu.IsOpen = true;

        void Rotate(double degrees)
        {
            var transforms = _selectedAnnotations.Where(item => item is not Image).Select(item =>
                (Element: item, Before: item.RenderTransform, Origin: item.RenderTransformOrigin,
                    After: new RotateTransform(((item.RenderTransform as RotateTransform)?.Angle ?? 0) + degrees))).ToArray();
            Edit(() =>
            {
                foreach (var item in transforms) { item.Element.RenderTransformOrigin = new Point(0.5, 0.5); item.Element.RenderTransform = item.After; }
            }, () =>
            {
                foreach (var item in transforms) { item.Element.RenderTransformOrigin = item.Origin; item.Element.RenderTransform = item.Before; }
            });
        }

        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
    }

    private void Edit(Action apply, Action undo)
    {
        _undo.Execute(new DelegateCommand(apply, undo));
        RefreshAnnotationEdits();
    }

    private void RefreshAnnotationEdits()
    {
        _selectedAnnotations.RemoveWhere(element => !_annotations.Contains(element));
        RefreshAnnotationFilters();
        UpdateHistoryButtons();
        UpdateAnnotationOutlines();
        SyncAnnotationToolbar();
    }

    private static void ApplyGaussianBlur(byte[] pixels, int width, int height, int stride, int radius)
    {
        var sigma = Math.Max(1, radius / 2d);
        var weights = Enumerable.Range(-radius, radius * 2 + 1)
            .Select(offset => Math.Exp(-(offset * offset) / (2 * sigma * sigma))).ToArray();
        var sum = weights.Sum();
        for (var i = 0; i < weights.Length; i++) weights[i] /= sum;
        var horizontal = new byte[pixels.Length];
        Pass(pixels, horizontal, vertical: false);
        Pass(horizontal, pixels, vertical: true);

        void Pass(byte[] source, byte[] destination, bool vertical)
        {
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                double b = 0, g = 0, r = 0, a = 0;
                for (var offset = -radius; offset <= radius; offset++)
                {
                    var sample = vertical ? Math.Clamp(y + offset, 0, height - 1) * stride + x * 4
                        : y * stride + Math.Clamp(x + offset, 0, width - 1) * 4;
                    var weight = weights[offset + radius];
                    b += source[sample] * weight;
                    g += source[sample + 1] * weight;
                    r += source[sample + 2] * weight;
                    a += source[sample + 3] * weight;
                }
                var index = y * stride + x * 4;
                destination[index] = (byte)Math.Round(b);
                destination[index + 1] = (byte)Math.Round(g);
                destination[index + 2] = (byte)Math.Round(r);
                destination[index + 3] = (byte)Math.Round(a);
            }
        }
    }
}
