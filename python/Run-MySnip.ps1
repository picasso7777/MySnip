<#
.SYNOPSIS
    一鍵安裝依賴並啟動 MySnip Python 版
#>
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
foreach ($candidate in $pythonCandidates) {
    try {
        $ver = & $candidate --version 2>&1
        if ($ver -match "Python 3\.\d+") {
            $python = $candidate
            break
        }
    } catch {}
}

if (-not $python) {
    Write-Host "找不到 Python 3.10+。" -ForegroundColor Red
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

# 安裝依賴
Write-Host "安裝依賴套件..." -ForegroundColor Cyan
& $python -m pip install --quiet -r "$scriptDir\requirements.txt"
if ($LASTEXITCODE -ne 0) {
    Write-Host "安裝依賴失敗，請確認 pip 可用。" -ForegroundColor Red
    pause
    exit 1
}

# 啟動
Write-Host "啟動 MySnip..." -ForegroundColor Green
& $python "$scriptDir\mysnip.py"
