using System.Windows.Input;
using MySnip.Core.Models;

namespace MySnip.App.Services;

internal static class ShortcutText
{
    public static bool TryParse(string text, out HotkeyGesture gesture)
    {
        gesture = HotkeyGesture.CaptureDefault;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var flags = HotkeyModifiers.NoRepeat;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            var flag = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "SHIFT" => HotkeyModifiers.Shift,
                "ALT" => HotkeyModifiers.Alt,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None
            };
            if (flag == HotkeyModifiers.None || flags.HasFlag(flag)) return false;
            flags |= flag;
        }
        var keyText = parts[^1];
        if (keyText.Length == 1 && char.IsAsciiDigit(keyText[0])) keyText = "D" + keyText;
        if (keyText.Length == 0 || char.IsAsciiDigit(keyText[0]) || !Enum.TryParse<Key>(keyText, true, out var key)) return false;
        var candidate = new HotkeyGesture((uint)KeyInterop.VirtualKeyFromKey(key), flags);
        if (!HotkeyValidator.IsSupported(candidate)) return false;
        gesture = candidate;
        return true;
    }

    public static string Format(HotkeyGesture gesture)
    {
        var key = KeyInterop.KeyFromVirtualKey((int)gesture.VirtualKey);
        var parts = new List<string>(5);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}
