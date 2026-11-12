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

    internal Brush Outline { get; init; } = Brushes.Black;
    internal double OutlineWidth { get; init; }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = Format();
        return new Size(text.WidthIncludingTrailingWhitespace + OutlineWidth, text.Height + OutlineWidth);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Background is not null) drawingContext.DrawRectangle(Background, null, new Rect(RenderSize));
        var text = Format();
        if (OutlineWidth <= 0) drawingContext.DrawText(text, new Point());
        else drawingContext.DrawGeometry(Foreground, new Pen(Outline, OutlineWidth) { LineJoin = PenLineJoin.Round },
            text.BuildGeometry(new Point(OutlineWidth / 2, OutlineWidth / 2)));
    }

    private FormattedText Format() => new(Text, CultureInfo.CurrentUICulture, FlowDirection,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground, 1);
}
