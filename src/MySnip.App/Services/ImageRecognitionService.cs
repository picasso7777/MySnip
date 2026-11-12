using System.Diagnostics;
using System.Text;
using System.Windows.Media.Imaging;

namespace MySnip.App.Services;

internal static class ImageRecognitionService
{
    private const string OcrScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
        $null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType=WindowsRuntime]
        $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } | Select-Object -First 1
        function Await($operation, $type) {
            $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
            $task.GetAwaiter().GetResult()
        }
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
        if (-not $engine) { throw 'No Windows OCR language is installed for this user.' }
        $file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($env:MYSNIP_RECOGNITION_IMAGE)) ([Windows.Storage.StorageFile])
        $stream = Await ($file.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
        try {
            $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            try {
                $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime])
                $result.Lines | ForEach-Object { [Console]::WriteLine($_.Text) }
            } finally { $bitmap.Dispose() }
        } finally { $stream.Dispose() }
        """;

    private const string BarcodeScript = """
        import os
        import sys
        sys.stdout.reconfigure(encoding='utf-8')
        path = os.environ['MYSNIP_RECOGNITION_IMAGE']
        try:
            import zxingcpp
        except ImportError:
            try:
                import cv2
                import numpy as np
            except ImportError:
                sys.exit('Install zxing-cpp and Pillow in the Python environment on PATH.')
            image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
            if image is None:
                sys.exit('Cannot load the selected image.')
            qr = cv2.QRCodeDetector()
            codes = []
            if hasattr(qr, 'detectAndDecodeMulti'):
                success, texts, _, _ = qr.detectAndDecodeMulti(image)
                if success:
                    codes.extend(text for text in texts if text)
            if not codes:
                text, _, _ = qr.detectAndDecode(image)
                if text:
                    codes.append(text)
            for text in dict.fromkeys(codes):
                print('QR: ' + text)
            barcode = getattr(cv2, 'barcode_BarcodeDetector', None)
            if barcode is not None:
                detector = barcode()
                if hasattr(detector, 'detectAndDecodeWithType'):
                    success, texts, formats, _ = detector.detectAndDecodeWithType(image)
                    if success:
                        for text, fmt in zip(texts, formats):
                            if text:
                                print(str(fmt) + ': ' + text)
        else:
            from PIL import Image
            with Image.open(path) as source:
                for code in zxingcpp.read_barcodes(source.convert('RGB')):
                    print(str(code.format) + ': ' + code.text)
        """;

    public static Task<string> ExtractTextAsync(BitmapSource source)
    {
        var width = source.PixelWidth;
        var height = source.PixelHeight;
        var factor = Math.Min(1d, 3900d / Math.Max(width, height));
        if (factor < 1) source = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(factor, factor));
        var command = Convert.ToBase64String(Encoding.Unicode.GetBytes(OcrScript));
        return RunAsync(source, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", command]);
    }

    public static Task<string> DecodeCodesAsync(BitmapSource source) => RunAsync(source, "python.exe", ["-c", BarcodeScript]);

    private static async Task<string> RunAsync(BitmapSource source, string executable, string[] arguments)
    {
        var path = Path.Combine(Path.GetTempPath(), $"MySnip-recognize-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, ClipboardService.EncodePng(source));
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            info.Environment["MYSNIP_RECOGNITION_IMAGE"] = path;
            using var process = Process.Start(info) ?? throw new InvalidOperationException("無法啟動辨識程式。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                throw new TimeoutException("辨識逾時。");
            }
            if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
            return (await output).Trim();
        }
        finally
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Cannot remove recognition input: {0}", ex.Message); }
        }
    }
}
