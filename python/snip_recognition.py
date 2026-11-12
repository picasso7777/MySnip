"""Windows OCR and local barcode decoding."""

from __future__ import annotations

import base64
import os
from pathlib import Path
import subprocess
import tempfile

from snip_images import RESAMPLE


OCR = r"""
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
"""


def recognize(image):
    with tempfile.TemporaryDirectory(prefix="MySnip-OCR-") as directory:
        path = Path(directory) / "image.png"
        source = image.copy()
        source.thumbnail((3900, 3900), RESAMPLE.LANCZOS)
        source.save(path)
        shell = Path(os.environ.get("WINDIR", r"C:\Windows")) / "System32/WindowsPowerShell/v1.0/powershell.exe"
        code = base64.b64encode(OCR.encode("utf-16-le")).decode("ascii")
        result = subprocess.run([str(shell), "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", code],
                                env={**os.environ, "MYSNIP_RECOGNITION_IMAGE": str(path)}, capture_output=True,
                                encoding="utf-8", errors="replace", timeout=25, creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise RuntimeError(result.stderr.strip() or "Windows OCR 失敗。")
        return result.stdout.strip()


def decode(image):
    try:
        import zxingcpp
    except ImportError:
        return _decode_with_opencv(image)
    return "\n".join(f"{code.format}: {code.text}" for code in zxingcpp.read_barcodes(image.convert("RGB")))


def _decode_with_opencv(image):
    try:
        import numpy as np
        import cv2
    except ImportError as error:
        external = _decode_with_installed_python(image)
        if external is not None:
            return external
        raise RuntimeError("請安裝 zxing-cpp，或使用具備條碼解碼功能的 OpenCV。") from error
    source = np.asarray(image.convert("RGB"))[:, :, ::-1].copy()
    detector = cv2.QRCodeDetector()
    codes = []
    if hasattr(detector, "detectAndDecodeMulti"):
        success, texts, _, _ = detector.detectAndDecodeMulti(source)
        if success:
            codes.extend(("QR", text) for text in texts if text)
    if not codes:
        text, _, _ = detector.detectAndDecode(source)
        if text:
            codes.append(("QR", text))
    factory = getattr(cv2, "barcode_BarcodeDetector", None)
    if factory:
        barcode = factory()
        if hasattr(barcode, "detectAndDecodeWithType"):
            success, texts, formats, _ = barcode.detectAndDecodeWithType(source)
            if success:
                codes.extend((kind, text) for kind, text in zip(formats, texts) if text)
    return "\n".join(f"{kind}: {text}" for kind, text in dict.fromkeys(codes))


def _decode_with_installed_python(image):
    import shutil
    import sys

    runtime = Path(sys.executable).resolve().parent
    app = Path(__file__).resolve().parent
    if app != runtime.parent / "app" or not (runtime.parent / ".ready").is_file():
        return None
    executable = shutil.which("python.exe", path=os.environ.get("MYSNIP_SYSTEM_PATH"))
    if not executable or Path(executable).parent.name.casefold() == "windowsapps" or Path(executable).resolve().parent == runtime:
        return None
    script = ("import sys; sys.stdout.reconfigure(encoding='utf-8'); sys.path.insert(0, sys.argv[1]); "
              "from PIL import Image; from snip_recognition import decode; print(decode(Image.open(sys.argv[2])))")
    with tempfile.TemporaryDirectory() as folder:
        path = Path(folder) / "image.png"
        image.save(path)
        try:
            process = subprocess.run([executable, "-B", "-c", script, str(app), str(path)], capture_output=True,
                                     encoding="utf-8", errors="replace", timeout=25, creationflags=subprocess.CREATE_NO_WINDOW)
        except (OSError, subprocess.TimeoutExpired):
            return None
    return process.stdout.strip() if process.returncode == 0 else None
