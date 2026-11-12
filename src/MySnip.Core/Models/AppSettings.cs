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
    private const HotkeyModifiers Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Windows;

    public static bool IsSupported(HotkeyGesture? gesture)
    {
        if (gesture is null || (gesture.Modifiers & ~(Modifiers | HotkeyModifiers.NoRepeat)) != 0) return false;
        var key = gesture.VirtualKey;
        if (key is >= 0x70 and <= 0x7B) return true;
        if ((gesture.Modifiers & (Modifiers & ~HotkeyModifiers.Shift)) == 0) return false;
        return key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0x7C and <= 0x87 or
            >= 0xBA and <= 0xE2 or 0x08 or 0x09 or 0x0D or 0x1B or >= 0x20 and <= 0x2E;
    }

    public static bool AreDistinct(HotkeyGesture first, HotkeyGesture second) =>
        first.VirtualKey != second.VirtualKey || (first.Modifiers & Modifiers) != (second.Modifiers & Modifiers);
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
            CaptureHotkey = capture!,
            PasteHotkey = paste!,
            CaptureDelaySeconds = Math.Clamp(settings.CaptureDelaySeconds, 1, 60),
            CaptureHistoryLimit = Math.Clamp(settings.CaptureHistoryLimit, 0, 100),
            HotCorners = settings.HotCorners ?? new HotCornerSettings(),
            Advanced = (settings.Advanced ?? new AdvancedPreferences()).Normalize(),
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
    public HotkeyGesture? VisibilityHotkey { get; init; }
    public bool VisibilityHotkeyDisabled { get; init; }

    public HotkeyGesture? EffectiveVisibilityHotkey()
    {
        if (VisibilityHotkeyDisabled) return null;
        if (VisibilityHotkey is { } visibility && HotkeyValidator.IsSupported(visibility)) return visibility;
        var derived = PasteHotkey with { Modifiers = PasteHotkey.Modifiers | HotkeyModifiers.Shift };
        if (PasteHotkey.Modifiers.HasFlag(HotkeyModifiers.Shift) || !HotkeyValidator.IsSupported(derived) ||
            !HotkeyValidator.AreDistinct(CaptureHotkey, derived) ||
            Advanced.CommandHotkeys.Any(binding => !HotkeyValidator.AreDistinct(binding.Gesture, derived))) return null;
        return derived;
    }
    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "MySnip");
    public ToolDefaults Tools { get; init; } = new();
    public bool StartWithWindows { get; init; }
    public int CaptureDelaySeconds { get; init; } = 3;
    public int CaptureHistoryLimit { get; init; } = 20;
    public bool AutoSaveCaptures { get; init; }
    public bool IncludeCursor { get; init; }
    public bool RestoreStickers { get; init; } = true;
    public HotCornerSettings HotCorners { get; init; } = new();
    public AdvancedPreferences Advanced { get; init; } = new();
}

public enum HotCornerAction { Disabled, Capture, Paste, ToggleStickers }

public sealed record HotCornerSettings
{
    public HotCornerAction TopLeft { get; init; }
    public HotCornerAction TopRight { get; init; }
    public HotCornerAction BottomLeft { get; init; }
    public HotCornerAction BottomRight { get; init; }
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
    Eraser,
    RoundedRectangle,
    DoubleArrow,
    Number,
    Magnify,
    LineStrip,
    HighlighterEllipse
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
