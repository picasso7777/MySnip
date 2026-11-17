using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MySnip.App.Windows;

internal sealed class OutlinedAnnotationText : Control
{
    internal static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(OutlinedAnnotationText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure |
            FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    internal const double TextPadding = 4;

    internal Brush Outline { get; set; } = Brushes.Black;
    internal double OutlineWidth { get; set; }

    internal void SetOutline(Brush brush, double width)
    {
        Outline = brush;
        OutlineWidth = width;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = Format(Text, constraint.Width);
        return new Size(text.WidthIncludingTrailingWhitespace + TextPadding * 2 + OutlineWidth,
            text.Height + TextPadding * 2 + OutlineWidth);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is not null) drawingContext.DrawRectangle(Background, null, new Rect(RenderSize));
        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        var text = Format(Text, RenderSize.Width);
        var padding = TextPadding + OutlineWidth / 2;
        var origin = new Point(padding, padding);
        if (OutlineWidth <= 0) drawingContext.DrawText(text, origin);
        else drawingContext.DrawGeometry(Foreground, new Pen(Outline, OutlineWidth) { LineJoin = PenLineJoin.Round },
            text.BuildGeometry(origin));
        drawingContext.Pop();
    }

    internal double FitFontSize(double boxHeight)
    {
        double minimum = 8, maximum = 144;
        // Measure a real line in the chosen typeface, including space for descenders and padding.
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var available = boxHeight - TextPadding * 2 - OutlineWidth;
        for (var i = 0; i < 12; i++)
        {
            var size = (minimum + maximum) / 2;
            var line = new FormattedText("\u200b", CultureInfo.CurrentUICulture, FlowDirection, typeface, size, Foreground, 1);
            if (line.Height <= available) minimum = size;
            else maximum = size;
        }
        return Math.Floor(minimum * 100) / 100;
    }

    internal double HeightForText(string value, double width) =>
        Format(value + "\u200b", width).Height + TextPadding * 2 + OutlineWidth;

    private FormattedText Format(string value, double width)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, 1);
        if (double.IsFinite(width)) text.MaxTextWidth = Math.Max(1, width - TextPadding * 2 - OutlineWidth);
        return text;
    }
}
