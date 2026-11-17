using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MySnip.Core.Models;

namespace MySnip.App.Windows;

public sealed partial class CaptureOverlayWindow
{
    private System.Windows.Shapes.Rectangle? _textBoxPreview;
    private TextBox? _textEditor;
    private OutlinedAnnotationText? _textBeingEdited;
    private bool _existingText;
    private bool _resumeTextAfterResize;
    private const double MinimumTextBoxSize = 8;
    private Point? _inlineTextPosition;
    private Point _inlineTextMoveOrigin;
    private int _inlineTextCaret;
    private System.Windows.Shapes.Line? _textCaret;
    private System.Windows.Threading.DispatcherTimer? _textCaretBlink;

    private const double TextHandleRadius = 6.5;
    private static readonly Vector TextDeleteCorner = new(1, -1);
    private static readonly Brush TextHandleBlue = new SolidColorBrush(Color.FromRgb(26, 132, 255));

    private static bool IsTextCorner(Vector direction) => direction.X != 0 && direction.Y != 0;

    private static bool IsTextDeleteHandle(FrameworkElement element, Vector direction) =>
        element is OutlinedAnnotationText && direction == TextDeleteCorner;

    private bool IsInTextFrame(Point point)
    {
        if (!_isSelectionCommitted || _tool == AnnotationTool.Eraser || ResizeTarget is not OutlinedAnnotationText text) return false;
        var bounds = AnnotationResizeBounds((FrameworkElement?)_textEditor ?? text);
        var center = ShapeHandlePosition(text, bounds, new Vector());
        return bounds.Contains(center + RotateShapeOffset(text, point - center, inverse: true));
    }

    private void StartInlineTextMove(Point point, int caret)
    {
        if (_textEditor is not { } editor || !InputCanvas.CaptureMouse()) return;
        _start = point;
        _inlineTextMoveOrigin = new Point(GetLeft(editor), GetTop(editor));
        _inlineTextCaret = caret;
        _dragMode = DragMode.MoveInlineText;
        InputCanvas.Cursor = Cursors.SizeAll;
    }

    private void MoveInlineText(Point point)
    {
        var delta = point - _start;
        _inlineTextPosition = _inlineTextMoveOrigin + delta;
        UpdateTextEditorBounds();
    }

    private void EndInlineTextMove(Point point, bool cancel = false)
    {
        var delta = point - _start;
        var clicked = !cancel && Math.Abs(delta.X) < 2 && Math.Abs(delta.Y) < 2;
        MoveInlineText(cancel || clicked ? _start : point);
        _dragMode = DragMode.None;
        InputCanvas.ReleaseMouseCapture();
        if (_textEditor is { } editor)
        {
            editor.Focus();
            if (clicked && _inlineTextCaret >= 0) editor.CaretIndex = _inlineTextCaret;
            ResetTextCaret();
        }
        UpdatePointerCursor(point);
    }

    private void DeleteTextFromHandle(OutlinedAnnotationText text)
    {
        FinishTextEditor(commit: false);
        if (_annotations.Contains(text)) DeleteAnnotation(text);
        Focus();
        UpdatePointerCursor(_currentPoint);
    }

    private void DrawTextChrome(OutlinedAnnotationText text, Rect bounds)
    {
        var border = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.Black, StrokeThickness = 1, RenderTransform = text.RenderTransform,
            RenderTransformOrigin = new Point(.5, .5)
        };
        SetRect(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        TextChromeCanvas.Children.Add(border);

        foreach (var direction in ShapeHandleDirections.Where(IsTextCorner))
        {
            var position = ShapeHandlePosition(text, bounds, direction);
            var badge = new System.Windows.Shapes.Ellipse { Fill = TextHandleBlue, Stroke = Brushes.White, StrokeThickness = 1 };
            SetRect(badge, position.X - TextHandleRadius, position.Y - TextHandleRadius, TextHandleRadius * 2, TextHandleRadius * 2);
            TextChromeCanvas.Children.Add(badge);

            var glyph = new StreamGeometry();
            using (var drawing = glyph.Open())
            {
                if (IsTextDeleteHandle(text, direction))
                {
                    Segment(new Vector(-2.5, -2.5), new Vector(2.5, 2.5));
                    Segment(new Vector(2.5, -2.5), new Vector(-2.5, 2.5));
                }
                else
                {
                    var tip = direction * 2.7;
                    Segment(-tip, tip);
                    Segment(tip - new Vector(direction.X * 2.4, 0), tip);
                    Segment(tip - new Vector(0, direction.Y * 2.4), tip);
                    Segment(-tip + new Vector(direction.X * 2.4, 0), -tip);
                    Segment(-tip + new Vector(0, direction.Y * 2.4), -tip);
                }

                void Segment(Vector start, Vector end)
                {
                    drawing.BeginFigure(position + start, false, false);
                    drawing.LineTo(position + end, true, false);
                }
            }
            glyph.Freeze();
            TextChromeCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = glyph, Stroke = Brushes.White, StrokeThickness = 1,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
    }

    private static readonly ControlTemplate InlineTextEditorTemplate = CreateTextEditorTemplate();

    private static ControlTemplate CreateTextEditorTemplate()
    {
        // Keep text input and IME support while excluding the theme's clear button and focus underline.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        content.SetValue(Control.FocusableProperty, false);
        border.AppendChild(content);
        return new ControlTemplate(typeof(TextBox)) { VisualTree = border };
    }

    private FrameworkElement? ResizeTarget => _textBeingEdited ?? (FrameworkElement?)SelectedResizableShape()
        ?? (SelectedAnnotation() as OutlinedAnnotationText);

    private static Rect AnnotationResizeBounds(FrameworkElement element) =>
        new(GetLeft(element), GetTop(element), double.IsFinite(element.Width) ? element.Width : Math.Max(8, element.ActualWidth),
            double.IsFinite(element.Height) ? element.Height : Math.Max(8, element.ActualHeight));

    private void UpdateTextCornerResize(OutlinedAnnotationText text, Rect original, Vector handle, Vector delta, double fontSize)
    {
        var diagonal = new Vector(handle.X * original.Width, handle.Y * original.Height);
        var minimum = Math.Min(1, Math.Max(MinimumTextBoxSize / Math.Min(original.Width, original.Height), 8 / fontSize));
        var maximum = Math.Max(1, 144 / fontSize);
        var scale = Math.Clamp(1 + Vector.Multiply(delta, diagonal) / diagonal.LengthSquared, minimum, maximum);
        var width = original.Width * scale;
        var height = original.Height * scale;
        var shift = RotateShapeOffset(text, new Vector(handle.X * (width - original.Width) / 2,
            handle.Y * (height - original.Height) / 2));
        text.FontSize = fontSize * scale;
        SetRect(text, original.X + (original.Width - width) / 2 + shift.X,
            original.Y + (original.Height - height) / 2 + shift.Y, width, height);
    }

    private static void SetTextGeometry(OutlinedAnnotationText text, Rect bounds, double fontSize)
    {
        text.FontSize = fontSize;
        SetRect(text, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void ResumeTextEditor(OutlinedAnnotationText text)
    {
        if (!_resumeTextAfterResize) return;
        _resumeTextAfterResize = false;
        OpenTextEditor(text, existing: true);
    }

    private void StartTextBox()
    {
        _dragMode = DragMode.DrawText;
        _textBoxPreview = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1, StrokeDashArray = [4, 2]
        };
        MaskCanvas.Children.Add(_textBoxPreview);
        UpdateTextBoxPreview(_start);
    }

    private void UpdateTextBoxPreview(Point point)
    {
        if (_textBoxPreview is not { } preview) return;
        var bounds = new Rect(_start, point);
        SetRect(preview, bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private void OpenNewTextBox(Point point)
    {
        RemoveTextBoxPreview();
        var bounds = new Rect(_start, point);
        var width = bounds.Width >= MinimumTextBoxSize ? bounds.Width : Math.Min(220, CanvasBounds.Right - bounds.Left);
        var height = bounds.Height >= MinimumTextBoxSize ? bounds.Height : Math.Min(44, CanvasBounds.Bottom - bounds.Top);
        var text = new OutlinedAnnotationText
        {
            Width = Math.Max(8, width), Height = Math.Max(8, height),
            FontFamily = FontFamilyPicker.SelectedItem as FontFamily ?? new FontFamily("Microsoft JhengHei UI"),
            FontSize = SelectedFontSize(), FontWeight = BoldToggle.IsChecked == true ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = ItalicToggle.IsChecked == true ? FontStyles.Italic : FontStyles.Normal,
            Foreground = new SolidColorBrush(_drawingColor), Opacity = _drawingOpacity,
            Background = TextBackground.IsChecked == true ? Brushes.White : Brushes.Transparent,
            Outline = new SolidColorBrush(_outlineColor), OutlineWidth = _outlineWidth,
            RenderTransformOrigin = new Point(.5, .5)
        };
        if (bounds.Height >= MinimumTextBoxSize) text.FontSize = text.FitFontSize(text.Height);
        SetPosition(text, new Point(Math.Max(0, Math.Min(bounds.Left, CanvasBounds.Right - text.Width)),
            Math.Max(0, Math.Min(bounds.Top, CanvasBounds.Bottom - text.Height))));
        OpenTextEditor(text, existing: false);
    }

    private void RemoveTextBoxPreview()
    {
        if (_textBoxPreview is not { } preview) return;
        MaskCanvas.Children.Remove(preview);
        _textBoxPreview = null;
    }

    private void EditAnnotationText(OutlinedAnnotationText text) => OpenTextEditor(text, existing: true);

    private void OpenTextEditor(OutlinedAnnotationText text, bool existing)
    {
        FinishTextEditor();
        _textBeingEdited = text;
        _inlineTextPosition = null;
        _existingText = existing;
        if (existing) text.Visibility = Visibility.Hidden;

        var editor = new TextBox
        {
            Text = text.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Template = InlineTextEditorTemplate, FocusVisualStyle = null, ForceCursor = true,
            BorderThickness = new Thickness(0), Padding = new Thickness(OutlinedAnnotationText.TextPadding),
            Background = text.Background, Foreground = text.Foreground, CaretBrush = Brushes.Transparent,
            FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight, FontStyle = text.FontStyle,
            Opacity = text.Opacity, RenderTransform = text.RenderTransform, RenderTransformOrigin = new Point(.5, .5),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _textEditor = editor;
        editor.TextChanged += (_, _) =>
        {
            UpdateTextEditorBounds();
            ResetTextCaret();
        };
        editor.SelectionChanged += (_, _) => ResetTextCaret();
        editor.GotKeyboardFocus += (_, _) => ResetTextCaret();
        editor.Loaded += (_, _) => ResetTextCaret();
        editor.LayoutUpdated += (_, _) => PositionTextCaret();
        editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var point = PointerPosition(e);
            if (HitAnnotationHandle(point) is not null)
            {
                e.Handled = true;
                Input_MouseLeftButtonDown(InputCanvas, e);
            }
            else if (IsInTextFrame(point))
            {
                e.Handled = true;
                StartInlineTextMove(point, editor.GetCharacterIndexFromPoint(e.GetPosition(editor), snapToText: true));
            }
        };
        editor.PreviewMouseMove += (_, e) =>
        {
            UpdatePointerCursor(PointerPosition(e));
            editor.Cursor = InputCanvas.Cursor;
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_dragMode == DragMode.MoveInlineText) { EndInlineTextMove(_start, cancel: true); return; }
                FinishTextEditor(commit: false);
                Focus();
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                FinishTextEditor();
                Focus();
            }
        };
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (_textEditor == editor && _dragMode != DragMode.MoveInlineText) FinishTextEditor();
        };
        InputCanvas.Children.Add(editor);
        _textCaret = new System.Windows.Shapes.Line
        {
            StrokeThickness = 2, IsHitTestVisible = false, SnapsToDevicePixels = true, Visibility = Visibility.Hidden
        };
        InputCanvas.Children.Add(_textCaret);
        _textCaretBlink = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _textCaretBlink.Tick += (_, _) =>
        {
            if (_textCaret is not { } caret) return;
            caret.Visibility = caret.Visibility == Visibility.Visible ? Visibility.Hidden : Visibility.Visible;
            PositionTextCaret();
        };
        SyncAnnotationToolbar();
        UpdateTextEditorBounds();
        editor.Focus();
        editor.CaretIndex = editor.Text.Length;
        ResetTextCaret();
    }

    private void ResetTextCaret()
    {
        if (_textCaret is not { } caret || _textCaretBlink is not { } blink) return;
        caret.Visibility = Visibility.Visible;
        PositionTextCaret();
        blink.Stop();
        blink.Start();
    }

    private void PositionTextCaret()
    {
        if (_textCaret is not { } caret || _textEditor is not { } editor || caret.Visibility != Visibility.Visible) return;
        if (!editor.IsKeyboardFocused || editor.SelectionLength != 0)
        {
            caret.Visibility = Visibility.Hidden;
            return;
        }

        var rect = editor.GetRectFromCharacterIndex(editor.CaretIndex);
        if (rect.IsEmpty || rect.Bottom < 0 || rect.Top >= editor.ActualHeight ||
            rect.X < 0 || rect.X > editor.ActualWidth)
        {
            caret.Visibility = Visibility.Hidden;
            return;
        }

        var transform = editor.TransformToAncestor(InputCanvas);
        var x = Math.Min(rect.X, Math.Max(0, editor.ActualWidth - 1));
        var start = transform.Transform(new Point(x, Math.Max(0, rect.Top)));
        var end = transform.Transform(new Point(x, Math.Min(editor.ActualHeight, rect.Bottom)));
        caret.X1 = start.X; caret.Y1 = start.Y;
        caret.X2 = end.X; caret.Y2 = end.Y;
        if (editor.Background == Brushes.White || _board == CaptureBoard.White) caret.Stroke = Brushes.Black;
        else
        {
            var background = CapturedPixelSampler.Sample(_frame, ToGlobal(ToPixel(start)));
            caret.Stroke = 0.299 * background.R + 0.587 * background.G + 0.114 * background.B >= 128
                ? Brushes.Black : Brushes.White;
        }
    }

    private void RemoveTextCaret()
    {
        _textCaretBlink?.Stop();
        _textCaretBlink = null;
        if (_textCaret is { } caret) InputCanvas.Children.Remove(caret);
        _textCaret = null;
    }

    private void UpdateTextEditorBounds()
    {
        if (_textEditor is not { } editor || _textBeingEdited is not { } text) return;
        var bounds = AnnotationResizeBounds(text);
        var width = bounds.Width;
        var height = bounds.Height;
        var position = _inlineTextPosition ?? bounds.TopLeft;
        var availableHeight = Math.Max(MinimumTextBoxSize, CanvasBounds.Bottom - position.Y);
        var formatted = new FormattedText(editor.Text + "\u200b", System.Globalization.CultureInfo.CurrentUICulture,
            editor.FlowDirection, new Typeface(editor.FontFamily, editor.FontStyle, editor.FontWeight, editor.FontStretch),
            editor.FontSize, editor.Foreground, 1) { MaxTextWidth = Math.Max(1, width - OutlinedAnnotationText.TextPadding * 2) };
        height = Math.Max(height, Math.Min(availableHeight, formatted.Height + OutlinedAnnotationText.TextPadding * 2));
        SetRect(editor, position.X, position.Y, width, height);
        UpdateAnnotationOutlines();
    }

    private void UpdateInlineTextStyle()
    {
        if (_textEditor is not { } editor || _textBeingEdited is not { } text) return;
        var style = ReadToolbarStyle();
        editor.FontFamily = style.Font;
        editor.FontSize = style.FontSize;
        editor.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        editor.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
        editor.Background = style.TextBackground ? Brushes.White : Brushes.Transparent;
        editor.Opacity = style.Opacity;
        UpdateTextEditorBounds();
        UpdateStyleLabels();
    }

    private void FinishTextEditor(bool commit = true)
    {
        if (_textEditor is not { } editor || _textBeingEdited is not { } text) return;
        var existing = _existingText;
        RemoveTextCaret();
        _textEditor = null;
        _textBeingEdited = null;
        _inlineTextPosition = null;
        InputCanvas.Children.Remove(editor);
        if (existing) text.Visibility = Visibility.Visible;
        if (commit && (existing || !string.IsNullOrWhiteSpace(editor.Text)))
        {
            if (!existing)
            {
                text.Text = editor.Text;
                text.FontFamily = editor.FontFamily;
                text.FontSize = editor.FontSize;
                text.FontWeight = editor.FontWeight;
                text.FontStyle = editor.FontStyle;
                text.Background = editor.Background;
                text.Opacity = editor.Opacity;
                text.Height = editor.Height;
                SetPosition(text, new Point(GetLeft(editor), GetTop(editor)));
                AddAnnotationCommand(text);
                SelectAnnotations([text]);
            }
            else if (string.IsNullOrWhiteSpace(editor.Text))
                DeleteAnnotation(text);
            else
                EditAnnotationValue((text.Text, AnnotationResizeBounds(text), text.FontFamily, text.FontSize, text.FontWeight, text.FontStyle, text.Background, text.Opacity),
                    (editor.Text, AnnotationResizeBounds(editor), editor.FontFamily, editor.FontSize, editor.FontWeight, editor.FontStyle, editor.Background, editor.Opacity),
                    value =>
                    {
                        (text.Text, _, text.FontFamily, text.FontSize, text.FontWeight, text.FontStyle, text.Background, text.Opacity) = value;
                        var bounds = value.Item2;
                        SetRect(text, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    });
        }
        SyncAnnotationToolbar();
        UpdateAnnotationOutlines();
    }
}
