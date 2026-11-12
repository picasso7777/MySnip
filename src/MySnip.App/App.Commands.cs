using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using MySnip.App.Services;
using MySnip.App.Windows;
using MySnip.Core.Models;

namespace MySnip.App;

public sealed partial class App
{
    private CancellationTokenSource? _commandListener;

    private async Task RunCommandAsync(InstanceCommand command)
    {
        try
        {
            var args = command.Arguments;
            switch (args.FirstOrDefault())
            {
                case null: ShowSettings(); break;
                case "open-preferences" when args.Length == 1: ShowSettings(); break;
                case "snip":
                    var capture = CaptureCommand.Parse(args[1..], command.WorkingDirectory);
                    if (capture.Target == CaptureTarget.Custom) await CaptureCustomRegionAsync(capture);
                    else await BeginCaptureAsync(command: capture);
                    break;
                case "paste": RunPasteCommand(args, command.WorkingDirectory); break;
                case "toggle-images" when args.Length == 1: _stickers.ToggleVisibility(); break;
                case "show-images" when args.Length == 1: _stickers.ShowHidden(); break;
                case "hide-images" when args.Length == 1: _stickers.HideAll(); break;
                case "no-click-through" when args.Length == 1: _stickers.DisableAllClickThrough(); break;
                case "toggle-click-through" when args.Length == 1: _stickers.ToggleClickThrough(); break;
                case "clear-snip-history" when args.Length == 1: _history.Trim(0); break;
                case "create-group" when args.Length == 1: EditGroupName(false); break;
                case "create-group" when args.Length == 2: _stickers.CreateGroup(args[1]); break;
                case "switch-group" when args.Length == 1: _stickers.SwitchToNextGroup(); break;
                case "switch-group" when args.Length == 2: _stickers.SwitchGroup(args[1]); break;
                case "switch-group-prev" when args.Length == 1: _stickers.SwitchToPreviousGroup(); break;
                case "empty-group" when args.Length == 1: _stickers.EmptyGroup(); break;
                case "show-group-manager" when args.Length == 1: ShowGroupManager(); break;
                case "show-capture-history" when args.Length == 1: ShowCaptureHistory(); break;
                case "show-tray-menu" when args.Length == 1: ShowTrayMenu(); break;
                case "enable-hotkeys" when args.Length == 1: SetHotkeysEnabled(true); break;
                case "disable-hotkeys" when args.Length == 1: SetHotkeysEnabled(false); break;
                case "enable-hot-corners" when args.Length == 1: SetCornersEnabled(true); break;
                case "disable-hot-corners" when args.Length == 1: SetCornersEnabled(false); break;
                case "exit" when args.Length == 1: _stickers.PrepareShutdown(); Shutdown(); break;
                default: throw new ArgumentException($"不支援的 MySnip 指令「{args[0]}」。");
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or NotSupportedException)
        { ShowBalloon(ex.Message); }
    }

    private async Task<bool> WriteCommandOutputsAsync(IReadOnlyList<CaptureOutputRequest> outputs, PixelRect region,
        BitmapSource image, bool silent)
    {
        var saved = false;
        foreach (var output in outputs)
        {
            switch (output.Destination)
            {
                case CaptureDestination.Pin:
                    _stickers.Create(image, new PixelPoint(region.X, region.Y));
                    break;
                case CaptureDestination.Clipboard:
                    _clipboard.SetPng(ClipboardService.EncodePng(image));
                    break;
                case CaptureDestination.OcrClipboard or CaptureDestination.BarcodeClipboard:
                    var text = output.Destination == CaptureDestination.OcrClipboard
                        ? await ImageRecognitionService.ExtractTextAsync(image)
                        : await ImageRecognitionService.DecodeCodesAsync(image);
                    if (text.Length > 0) Clipboard.SetText(text);
                    else if (!silent) ShowBalloon("選取區域中沒有辨識到內容。");
                    break;
                case CaptureDestination.QuickSave or CaptureDestination.CopyFile:
                    var path = CaptureOutput.QuickSave(image, _settings.OutputDirectory);
                    saved = true;
                    if (output.Destination == CaptureDestination.CopyFile)
                        _clipboard.SetPng(ClipboardService.EncodePng(image), path);
                    else if (!silent) ShowBalloon($"已儲存 {path}");
                    break;
                case CaptureDestination.FileDialog:
                    var dialog = new SaveFileDialog
                    {
                        InitialDirectory = _settings.OutputDirectory, Filter = "PNG 圖片|*.png|JPEG 圖片|*.jpg",
                        FileName = $"MySnip-{DateTime.Now:yyyyMMdd-HHmmss}"
                    };
                    if (dialog.ShowDialog() != true) break;
                    WriteFile(dialog.FileName);
                    break;
                case CaptureDestination.Printer:
                    var printer = new PrintDialog();
                    if (printer.ShowDialog() == true) CaptureOutput.Print(image, printer);
                    break;
                case CaptureDestination.File:
                    WriteFile(output.Path!);
                    break;
            }
        }
        return saved;

        void WriteFile(string path)
        {
            File.WriteAllBytes(path, BitmapFactory.Encode(image, Path.GetExtension(path)));
            saved = true;
        }
    }

    private async Task CaptureCustomRegionAsync(CaptureCommand? command = null)
    {
        var pointer = System.Windows.Forms.Control.MousePosition;
        command ??= new CaptureCommand();
        var value = InputDialog.Show(null, "自訂截圖範圍", "輸入 X Y 寬 高，以及選填的延遲秒數。",
            $"{pointer.X} {pointer.Y} 800 600", text => CaptureCommand.TryParseCustomArea(text, command.Delay, out _, out _));
        if (value is not null && CaptureCommand.TryParseCustomArea(value, command.Delay, out var region, out var delay))
            await BeginCaptureAsync(command: command with { Target = CaptureTarget.Area, Region = region, Delay = delay });
    }

    private PixelRect? ResolveRegion(CaptureCommand command, PixelRect? activeWindow)
    {
        var desktop = ScreenCaptureService.VirtualBounds();
        var cursor = System.Windows.Forms.Control.MousePosition;
        return command.Target switch
        {
            CaptureTarget.Area => command.Region,
            CaptureTarget.Size => CenteredRegion(command.Region),
            CaptureTarget.Full => desktop,
            CaptureTarget.Last => _history.Read().FirstOrDefault(record => !record.Canceled)?.Region ??
                throw new InvalidOperationException("沒有可重截的歷史範圍。"),
            CaptureTarget.ActiveWindow => activeWindow ?? throw new InvalidOperationException("沒有可截圖的作用中視窗。"),
            CaptureTarget.ActiveScreen => DesktopGeometry.DisplayAt(ScreenCaptureService.GetDisplayBounds(), new PixelPoint(cursor.X, cursor.Y), desktop),
            _ => null
        };

        PixelRect CenteredRegion(PixelRect size)
        {
            var width = Math.Min(size.Width, desktop.Width);
            var height = Math.Min(size.Height, desktop.Height);
            return new PixelRect(cursor.X - width / 2, cursor.Y - height / 2, width, height).MoveBy(0, 0, desktop);
        }
    }

    private void RunPasteCommand(string[] args, string workingDirectory)
    {
        PixelPoint? position = null;
        var parts = args.Skip(1).ToList();
        if (parts.FirstOrDefault() == "--pos") ReadPosition(0);
        var source = parts.FirstOrDefault() ?? "--clipboard";
        if (parts.Count > 0) parts.RemoveAt(0);
        var pos = parts.FindIndex(source is "--plain" or "--html" ? Math.Min(1, parts.Count) : 0, part => part == "--pos");
        if (pos >= 0)
        {
            if (position.HasValue || pos != parts.Count - 3) throw new ArgumentException("請指定有效的貼圖座標。");
            ReadPosition(pos);
        }
        switch (source)
        {
            case "--clipboard" when parts.Count == 0: PasteFromClipboard(position); break;
            case "--plain" when parts.Count == 1: _stickers.Create(StickerText.Render(parts[0]), position); break;
            case "--html" when parts.Count == 1: _stickers.Create(StickerText.Render("", parts[0]), position); break;
            case "--files" when parts.Count > 0:
                foreach (var path in parts) _stickers.Create(StickerContent.Load(Path.GetFullPath(path, workingDirectory)), position);
                break;
            default: throw new ArgumentException("請指定有效的貼圖來源。");
        }

        void ReadPosition(int index)
        {
            position = new PixelPoint(Number(parts, index + 1), Number(parts, index + 2));
            parts.RemoveRange(index, 3);
        }
    }

    private static int Number(IReadOnlyList<string> values, int index) =>
        int.TryParse(Value(values, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number : throw new ArgumentException("截圖或貼圖座標須為整數。");

    private static string Value(IReadOnlyList<string> values, int index) =>
        index < values.Count ? values[index] : throw new ArgumentException("命令列選項缺少參數。");
}
