namespace MySnip.Core.Models;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
    NoRepeat = 0x4000
}

public sealed record HotkeyGesture(uint VirtualKey, HotkeyModifiers Modifiers = HotkeyModifiers.NoRepeat)
{
    public static HotkeyGesture CaptureDefault => new(0x70); // F1
    public static HotkeyGesture PasteDefault => new(0x72); // F3
}

public static class HotkeyValidator
{
    public static bool IsSupported(HotkeyGesture gesture) => gesture.VirtualKey is >= 0x70 and <= 0x7B;
    public static bool AreDistinct(HotkeyGesture first, HotkeyGesture second) => first != second;
}

public static class SettingsValidator
{
    public static AppSettings Normalize(AppSettings settings)
    {
        var capture = HotkeyValidator.IsSupported(settings.CaptureHotkey) ? settings.CaptureHotkey : HotkeyGesture.CaptureDefault;
        var paste = HotkeyValidator.IsSupported(settings.PasteHotkey) ? settings.PasteHotkey : HotkeyGesture.PasteDefault;
        if (!HotkeyValidator.AreDistinct(capture, paste)) paste = HotkeyGesture.PasteDefault;
        if (!HotkeyValidator.AreDistinct(capture, paste)) capture = HotkeyGesture.CaptureDefault;

        var tools = settings.Tools ?? new ToolDefaults();
        return settings with
        {
            CaptureHotkey = capture,
            PasteHotkey = paste,
            OutputDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory) ? new AppSettings().OutputDirectory : settings.OutputDirectory,
            Tools = new ToolDefaults
            {
                Color = IsArgbHex(tools.Color) ? tools.Color : new ToolDefaults().Color,
                StrokeWidth = double.IsFinite(tools.StrokeWidth) ? Math.Clamp(tools.StrokeWidth, 1, 32) : 3,
                Opacity = double.IsFinite(tools.Opacity) ? Math.Clamp(tools.Opacity, 0.1, 1) : 1
            }
        };
    }

    private static bool IsArgbHex(string? value) => value is { Length: 9 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _);
}

public sealed record ToolDefaults
{
    public string Color { get; init; } = "#FFFF3B30";
    public double StrokeWidth { get; init; } = 3;
    public double Opacity { get; init; } = 1;
}

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public HotkeyGesture CaptureHotkey { get; init; } = HotkeyGesture.CaptureDefault;
    public HotkeyGesture PasteHotkey { get; init; } = HotkeyGesture.PasteDefault;
    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MySnip");
    public ToolDefaults Tools { get; init; } = new();
    public bool StartWithWindows { get; init; }
}

public sealed record CapturedFrame(PixelRect Bounds, int Stride, byte[] BgraPixels);

public enum CaptureCandidateKind
{
    TopLevelWindow,
    UiElement
}

/// <param name="Layer">Top-level z-order captured before the overlay is shown; zero is frontmost.</param>
public readonly record struct CaptureCandidate(PixelRect Bounds, CaptureCandidateKind Kind, int Layer = 0);

public enum AnnotationTool
{
    Select,
    Rectangle,
    FilledRectangle,
    Ellipse,
    FilledEllipse,
    Line,
    Arrow,
    OutlineArrow,
    FilledArrow,
    Pen,
    Highlighter,
    HighlighterRectangle,
    Text,
    Mosaic,
    Blur,
    Eraser
}

public sealed record StickerTransformState
{
    public double Scale { get; init; } = 1;
    public double Opacity { get; init; } = 1;
    public int RotationDegrees { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    public StickerTransformState Zoom(double factor) => this with { Scale = Math.Clamp(Scale * factor, 0.1, 8) };
    public StickerTransformState ChangeOpacity(double delta) => this with { Opacity = Math.Clamp(Opacity + delta, 0.1, 1) };
    public StickerTransformState RotateClockwise() => this with { RotationDegrees = (RotationDegrees + 90) % 360 };
}
