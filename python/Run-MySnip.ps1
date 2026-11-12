<#
.SYNOPSIS
    使用已安裝的 Python 啟動 MySnip，必要時安裝基本依賴。
#>
param(
    [switch]$InstallExtras,
    [string[]]$MySnipArguments = @()
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 找到可用的 Python
$pythonCandidates = @(
    "py"
    "python3"
    "python"
    "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe"
    "$env:LOCALAPPDATA\Programs\Python\Python311\python.exe"
    "$env:LOCALAPPDATA\Programs\Python\Python310\python.exe"
    "C:\Python312\python.exe"
    "C:\Python311\python.exe"
    "C:\Python310\python.exe"
)

$python = $null
$fallback = $null
foreach ($candidate in $pythonCandidates) {
    try {
        $command = Get-Command $candidate -ErrorAction SilentlyContinue
        if (-not $command -or $command.Source -like '*\Microsoft\WindowsApps\python*.exe') { continue }
        & $candidate -c "import sys, tkinter; sys.exit(sys.version_info < (3, 8))" 2>$null
        if ($LASTEXITCODE -eq 0) {
            if (-not $fallback) { $fallback = $candidate }
            & $candidate -c "import PIL, win32api, win32clipboard" 2>$null
            if ($LASTEXITCODE -eq 0) { $python = $candidate; break }
        }
    } catch {}
}
if (-not $python) { $python = $fallback }

if (-not $python) {
    Write-Host "找不到含 Tkinter 的 Python 3.8 以上版本。" -ForegroundColor Red
    Write-Host ""
    Write-Host "請從以下任一方式安裝 Python：" -ForegroundColor Yellow
    Write-Host "  1. https://www.python.org/downloads/ (勾選 Add to PATH)"
    Write-Host "  2. winget install Python.Python.3.12"
    Write-Host "  3. Microsoft Store 搜尋 Python 3.12"
    Write-Host ""
    pause
    exit 1
}

Write-Host "使用 Python: $python" -ForegroundColor Cyan
& $python --version

# 有相容依賴時可直接執行，避免每次啟動都連線。
try { & $python -c "import PIL, win32api, win32clipboard" 2>$null }
catch { $LASTEXITCODE = 1 }
if ($LASTEXITCODE -ne 0) {
    & $python -m pip install --quiet -r "$scriptDir\requirements.txt"
    if ($LASTEXITCODE -ne 0) { throw "無法安裝 MySnip Python 的基本依賴。" }
}
if ($InstallExtras) {
    & $python -m pip install --quiet mss zxing-cpp
    if ($LASTEXITCODE -ne 0) { throw "無法安裝螢幕擷取或條碼擴充套件。" }
}

# 啟動
Write-Host "啟動 MySnip..." -ForegroundColor Green
& $python "$scriptDir\mysnip.py" @MySnipArguments
