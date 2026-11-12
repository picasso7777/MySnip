using System.Globalization;

namespace MySnip.Core.Models;

public enum CaptureTarget { Interactive, Area, Size, Full, Last, ActiveWindow, ActiveScreen, Custom }
public enum CaptureDestination { Clipboard, Pin, QuickSave, FileDialog, OcrClipboard, BarcodeClipboard, CopyFile, Printer, Success, File }
public sealed record CaptureOutputRequest(CaptureDestination Destination, string? Path = null);

public sealed record CaptureCommand
{
    public CaptureTarget Target { get; init; }
    public PixelRect Region { get; init; }
    public double Delay { get; init; }
    public bool Hold { get; init; }
    public bool Shadow { get; init; }
    public bool Silent { get; init; }
    public bool SkipAutoSave { get; init; }
    public IReadOnlyList<CaptureOutputRequest> Outputs { get; init; } = [];

    public static CaptureCommand Parse(IReadOnlyList<string> args, string workingDirectory)
    {
        var command = new CaptureCommand();
        var outputs = new List<CaptureOutputRequest>();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--area":
                    SetTarget(CaptureTarget.Area);
                    command = command with { Region = Area(Integer(args, ++i), Integer(args, ++i), Integer(args, ++i), Integer(args, ++i)) };
                    break;
                case "--size":
                    SetTarget(CaptureTarget.Size);
                    command = command with { Region = Area(0, 0, Integer(args, ++i), Integer(args, ++i)) };
                    break;
                case "--full": SetTarget(CaptureTarget.Full); break;
                case "--last": SetTarget(CaptureTarget.Last); break;
                case "--active-window": SetTarget(CaptureTarget.ActiveWindow); break;
                case "--active-screen": SetTarget(CaptureTarget.ActiveScreen); break;
                case "--custom": SetTarget(CaptureTarget.Custom); break;
                case "--hold": command = command with { Hold = true }; break;
                case "--shadow": command = command with { Shadow = true }; break;
                case "--no-shadow": command = command with { Shadow = false }; break;
                case "--delay":
                    if (!TryDelay(Value(args, ++i), out var delay))
                        throw new ArgumentException("延遲時間須在 0–60 秒之間。");
                    command = command with { Delay = delay };
                    break;
                case "-o" or "--output":
                    foreach (var output in Value(args, ++i).Split(';', StringSplitOptions.TrimEntries))
                    {
                        if (output == "silent") command = command with { Silent = true };
                        else if (output == "no-auto-save") command = command with { SkipAutoSave = true };
                        else
                        {
                            var request = ParseOutput(output, workingDirectory);
                            if (!outputs.Contains(request)) outputs.Add(request);
                        }
                    }
                    break;
                default: throw new ArgumentException($"不支援的截圖選項「{args[i]}」。");
            }
        }
        return command with { Outputs = outputs.ToArray() };

        void SetTarget(CaptureTarget target)
        {
            if (command.Target != CaptureTarget.Interactive) throw new ArgumentException("每次只能指定一種截圖範圍。");
            command = command with { Target = target };
        }
    }

    public static bool TryParseCustomArea(string text, double defaultDelay, out PixelRect region, out double delay)
    {
        region = default;
        delay = defaultDelay;
        var parts = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is not (4 or 5) || !TryInteger(parts[0], out var x) || !TryInteger(parts[1], out var y) ||
            !TryInteger(parts[2], out var width) || !TryInteger(parts[3], out var height) ||
            !ValidArea(x, y, width, height) || parts.Length == 5 && !TryDelay(parts[4], out delay)) return false;
        region = new PixelRect(x, y, width, height);
        return true;
    }

    private static CaptureOutputRequest ParseOutput(string output, string workingDirectory)
    {
        var destination = output switch
        {
            "clipboard" => CaptureDestination.Clipboard,
            "pin" => CaptureDestination.Pin,
            "quick-save" => CaptureDestination.QuickSave,
            "file-dialog" => CaptureDestination.FileDialog,
            "ocr_clipboard" => CaptureDestination.OcrClipboard,
            "barcode_clipboard" => CaptureDestination.BarcodeClipboard,
            "copy-file" => CaptureDestination.CopyFile,
            "printer" => CaptureDestination.Printer,
            "success" => CaptureDestination.Success,
            _ => CaptureDestination.File
        };
        if (destination != CaptureDestination.File) return new CaptureOutputRequest(destination);
        if (Path.GetExtension(output).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg"))
            throw new ArgumentException($"不支援的截圖輸出「{output}」。");
        return new CaptureOutputRequest(destination, Path.GetFullPath(output, workingDirectory));
    }

    private static PixelRect Area(int x, int y, int width, int height) =>
        ValidArea(x, y, width, height) ? new PixelRect(x, y, width, height)
            : throw new ArgumentException("截圖位置或尺寸超出有效範圍。");

    private static bool ValidArea(int x, int y, int width, int height) =>
        width > 0 && height > 0 && (long)x + width <= int.MaxValue && (long)y + height <= int.MaxValue;

    private static bool TryDelay(string text, out double delay) =>
        double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out delay) && delay is >= 0 and <= 60;

    private static bool TryInteger(string text, out int result) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    private static int Integer(IReadOnlyList<string> args, int index) =>
        TryInteger(Value(args, index), out var result) ? result : throw new ArgumentException("截圖座標須為整數。");

    private static string Value(IReadOnlyList<string> args, int index) =>
        index < args.Count ? args[index] : throw new ArgumentException("命令列選項缺少參數。");
}
