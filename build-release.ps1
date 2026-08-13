[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertificateThumbprint = '',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$solution = Join-Path $taskRoot 'MySnip.sln'
$publishDir = Join-Path $taskRoot 'publish\win-x64'
$artifactsDir = Join-Path $taskRoot 'artifacts'
$distDir = Join-Path $taskRoot 'dist'

Write-Host '==> Cleaning generated outputs'
foreach ($target in @($publishDir, $artifactsDir, $distDir)) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($taskRoot) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [System.IO.Path]::GetFullPath($target)
    if (-not $resolvedTarget.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside project: $resolvedTarget"
    }
    if (Test-Path -LiteralPath $resolvedTarget) { Remove-Item -LiteralPath $resolvedTarget -Recurse -Force }
}
New-Item -ItemType Directory -Path $artifactsDir, $distDir -Force | Out-Null

Write-Host '==> Restoring'
$iconGenerator = Join-Path $taskRoot 'tools\Generate-MySnipIcon.ps1'
if (-not (Test-Path -LiteralPath $iconGenerator)) { throw "Icon generator not found: $iconGenerator" }
Write-Host '==> Generating MySnip icon'
& $iconGenerator
if (-not $?) { throw 'Icon generation failed' }

dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

Write-Host '==> Testing'
dotnet test $solution -c $Configuration --no-restore --logger "trx;LogFileName=MySnip-tests.trx" --results-directory $artifactsDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed' }

Write-Host '==> Publishing self-contained win-x64 application'
dotnet publish (Join-Path $taskRoot 'src\MySnip.App\MySnip.App.csproj') -c $Configuration -r win-x64 --self-contained true --no-restore -o $publishDir -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

if ($CertificateThumbprint) {
    $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signTool) { throw 'CertificateThumbprint was supplied but signtool.exe was not found.' }
    & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com (Join-Path $publishDir 'MySnip.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Application signing failed' }
}

if (-not $SkipInstaller) {
    Write-Host '==> Building Inno Setup installer'
    $isccCandidates = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if (-not $isccCandidates) { throw 'Inno Setup 6 compiler (ISCC.exe) was not found.' }
    & $isccCandidates (Join-Path $taskRoot 'installer\MySnip.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed' }

    if ($CertificateThumbprint) {
        $installer = Join-Path $distDir 'MySnip-Setup-x64.exe'
        & $signTool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /td SHA256 /tr http://timestamp.digicert.com $installer
        if ($LASTEXITCODE -ne 0) { throw 'Installer signing failed' }
    }
}

Write-Host '==> Release build completed'
Write-Host "Application: $publishDir"
if (-not $SkipInstaller) { Write-Host "Installer: $(Join-Path $distDir 'MySnip-Setup-x64.exe')" }
