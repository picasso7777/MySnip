[CmdletBinding()]
param(
    [string]$SvgPath = '',
    [string]$IcoPath = ''
)

$ErrorActionPreference = 'Stop'
$scriptRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($SvgPath)) { $SvgPath = Join-Path $scriptRoot '..\assets\MySnip.svg' }
if ([string]::IsNullOrWhiteSpace($IcoPath)) { $IcoPath = Join-Path $scriptRoot '..\assets\MySnip.ico' }
$svg = [System.IO.Path]::GetFullPath($SvgPath)
$ico = [System.IO.Path]::GetFullPath($IcoPath)
if (-not (Test-Path -LiteralPath $svg)) { throw "SVG source not found: $svg" }

# Render the original vector with WPF, then write a standards-compliant multi-size ICO.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = [System.Collections.Generic.List[byte[]]]::new()

function New-IconBitmap {
    param([int]$Size)

    $visual = [System.Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / 256d, $Size / 256d))

    $gradient = [System.Windows.Media.LinearGradientBrush]::new(
        [System.Windows.Media.Color]::FromRgb(38, 87, 215),
        [System.Windows.Media.Color]::FromRgb(21, 51, 154), 45)
    $context.DrawRoundedRectangle($gradient, $null, [System.Windows.Rect]::new(0, 0, 256, 256), 54, 54)

    $cornerPen = [System.Windows.Media.Pen]::new(
        [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(175, 200, 255)), 12)
    $cornerPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $cornerPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $cornerGeometry = [System.Windows.Media.StreamGeometry]::new()
    $geometryContext = $cornerGeometry.Open()
    $geometryContext.BeginFigure([System.Windows.Point]::new(55, 86), $false, $false)
    $geometryContext.LineTo([System.Windows.Point]::new(55, 60), $false, $false)
    $geometryContext.BezierTo([System.Windows.Point]::new(55, 52), [System.Windows.Point]::new(61, 46), [System.Windows.Point]::new(69, 46), $false, $false)
    $geometryContext.LineTo([System.Windows.Point]::new(95, 46), $false, $false)
    $geometryContext.Close()
    $context.DrawGeometry($null, $cornerPen, $cornerGeometry)
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(161, 46), [System.Windows.Point]::new(187, 46))
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(201, 60), [System.Windows.Point]::new(201, 86))
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(55, 170), [System.Windows.Point]::new(55, 196))
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(69, 210), [System.Windows.Point]::new(95, 210))
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(161, 210), [System.Windows.Point]::new(187, 210))
    $context.DrawLine($cornerPen, [System.Windows.Point]::new(201, 170), [System.Windows.Point]::new(201, 196))

    $white = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Colors]::White)
    $context.DrawRoundedRectangle($white, $null, [System.Windows.Rect]::new(70, 70, 116, 126), 16, 16)
    $pale = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(214, 225, 255))
    $context.DrawRoundedRectangle($pale, $null, [System.Windows.Rect]::new(86, 88, 84, 9), 4.5, 4.5)
    $context.DrawRoundedRectangle($pale, $null, [System.Windows.Rect]::new(86, 110, 58, 9), 4.5, 4.5)
    $panel = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(232, 238, 255))
    $context.DrawRoundedRectangle($panel, $null, [System.Windows.Rect]::new(86, 132, 74, 34), 8, 8)
    $mountain = [System.Windows.Media.StreamGeometry]::new()
    $mountainContext = $mountain.Open()
    $mountainContext.BeginFigure([System.Windows.Point]::new(95, 157), $true, $true)
    $mountainContext.LineTo([System.Windows.Point]::new(113, 142), $true, $false)
    $mountainContext.LineTo([System.Windows.Point]::new(127, 153), $true, $false)
    $mountainContext.LineTo([System.Windows.Point]::new(139, 143), $true, $false)
    $mountainContext.LineTo([System.Windows.Point]::new(153, 157), $true, $false)
    $mountainContext.Close()
    $mountainBrush = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(107, 142, 243))
    $context.DrawGeometry($mountainBrush, $null, $mountain)
    $context.DrawEllipse([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(255, 128, 109)), $null, [System.Windows.Point]::new(149, 143), 6, 6)

    $context.PushTransform([System.Windows.Media.RotateTransform]::new(14, 178, 178))
    $coral = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(255, 128, 109))
    $context.DrawEllipse($coral, $null, [System.Windows.Point]::new(178, 178), 39, 39)
    $context.DrawRoundedRectangle($coral, $null, [System.Windows.Rect]::new(161, 178, 34, 54), 10, 10)
    $stemPen = [System.Windows.Media.Pen]::new([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromRgb(184, 66, 69)), 8)
    $stemPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $stemPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $context.DrawLine($stemPen, [System.Windows.Point]::new(161, 233), [System.Windows.Point]::new(195, 233))
    $lightPen = [System.Windows.Media.Pen]::new([System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.Color]::FromArgb(190, 255, 255, 255)), 6)
    $lightPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
    $lightPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
    $context.DrawLine($lightPen, [System.Windows.Point]::new(165, 217), [System.Windows.Point]::new(191, 217))
    $context.Pop()
    $context.Pop()
    $context.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

foreach ($size in $sizes) {
    $bitmap = New-IconBitmap $size
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    try { $encoder.Save($stream); $pngs.Add($stream.ToArray()) }
    finally { $stream.Dispose() }
}

$directory = [System.IO.Path]::GetDirectoryName($ico)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$output = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $size = $sizes[$i]
    $png = $pngs[$i]
    $writer.Write([byte]($(if ($size -ge 256) { 0 } else { $size })))
    $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$png.Length)
    $writer.Write([uint32]$offset)
    $offset += $png.Length
}
foreach ($png in $pngs) { $writer.Write($png) }
$writer.Flush()
[System.IO.File]::WriteAllBytes($ico, $output.ToArray())
$writer.Dispose()
$output.Dispose()
Write-Host "Generated $ico"
