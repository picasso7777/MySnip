namespace MySnip.Core.Models;

public enum TrayAction { Menu, Capture, Paste, ToggleStickers, Settings, None }
public enum AppTheme { System, Light, Dark }
public enum StickerDesktopMode { PerDesktop, FollowActiveDesktop }
public enum StickerEscapeAction { Hide, Close, DisableClickThrough, None }
public enum SuperSnipOutput { Clipboard, Pin, Edit }

public sealed record CommandHotkey(HotkeyGesture Gesture, string Command, string IgnoreApplications = "");

public sealed record AdvancedPreferences
{
    public bool SuperSnip { get; init; }
    public SuperSnipOutput SuperSnipOutput { get; init; }
    public bool HideCaptureForSaveDialog { get; init; } = true;
    public bool RecordCanceledCaptures { get; init; }
    public bool CopyImageAsFile { get; init; }
    public StickerDesktopMode StickerDesktop { get; init; }
    public StickerEscapeAction StickerEscape { get; init; } = StickerEscapeAction.Hide;
    public StickerEscapeAction StickerShiftEscape { get; init; } = StickerEscapeAction.Close;
    public TrayAction TrayLeft { get; init; } = TrayAction.Capture;
    public TrayAction TrayMiddle { get; init; } = TrayAction.Paste;
    public TrayAction TrayRight { get; init; } = TrayAction.Menu;
    public AppTheme Theme { get; init; }
    public bool ShowMagnifier { get; init; } = true;
    public int MagnifierSize { get; init; } = 120;
    public int MagnifierSampleRadius { get; init; } = 7;
    public string MagnifierBackground { get; init; } = "#EB151515";
    public bool MagnifierCrosshair { get; init; } = true;
    public bool RememberToolColors { get; init; }
    public bool SelectNewAnnotation { get; init; }
    public int[] PenSizes { get; init; } = [2, 4, 8];
    public string[] Palette { get; init; } = [];
    public Dictionary<AnnotationTool, string> ToolColors { get; init; } = [];
    public string IgnoreApplications { get; init; } = "";
    public CommandHotkey[] CommandHotkeys { get; init; } = [];

    internal AdvancedPreferences Normalize() => this with
    {
        SuperSnipOutput = Defined(SuperSnipOutput),
        StickerDesktop = Defined(StickerDesktop),
        StickerEscape = Defined(StickerEscape),
        StickerShiftEscape = Defined(StickerShiftEscape),
        TrayLeft = Defined(TrayLeft),
        TrayMiddle = Defined(TrayMiddle),
        TrayRight = Defined(TrayRight),
        Theme = Defined(Theme),
        MagnifierSize = Math.Clamp(MagnifierSize, 72, 320),
        MagnifierSampleRadius = Math.Clamp(MagnifierSampleRadius, 2, 25),
        MagnifierBackground = IsColor(MagnifierBackground) ? MagnifierBackground : "#EB151515",
        PenSizes = NormalizePenSizes(PenSizes),
        Palette = (Palette ?? []).Where(IsColor).Take(40).ToArray(),
        ToolColors = (ToolColors ?? []).Where(pair => Enum.IsDefined(pair.Key) && IsColor(pair.Value)).ToDictionary(),
        IgnoreApplications = IgnoreApplications?.Trim() ?? "",
        CommandHotkeys = (CommandHotkeys ?? []).Where(binding => binding is not null &&
            HotkeyValidator.IsSupported(binding.Gesture) && !string.IsNullOrWhiteSpace(binding.Command)).Take(64).ToArray()
    };

    public static bool IsColor(string? value) => value is { Length: 9 } && value[0] == '#' &&
        uint.TryParse(value.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out _);

    private static int[] NormalizePenSizes(int[]? sizes) =>
        sizes is not { Length: > 0 } ? [2, 4, 8] : sizes.Take(5).Select(size => Math.Clamp(size, 1, 32)).ToArray();

    private static T Defined<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value : default;
}
