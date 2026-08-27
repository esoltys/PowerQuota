[CmdletBinding()]
param(
    [switch]$Register
)

$ErrorActionPreference = "Stop"

Write-Host "==> Stopping running PowerQuota extension instances..." -ForegroundColor Cyan
Stop-Process -Name "PowerQuota.CommandPalette" -Force -ErrorAction SilentlyContinue

$dotnetPath = "dotnet"
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    $dotnetPath = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

Write-Host "==> Building and publishing PowerQuota extension..." -ForegroundColor Cyan
& $dotnetPath publish "$PSScriptRoot\src\PowerQuota.CommandPalette\PowerQuota.CommandPalette.csproj" -c Release -r win-x64 --self-contained false

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

if ($Register) {
    Write-Host "==> Registering AppX package with Windows..." -ForegroundColor Cyan
    Stop-Process -Name "PowerQuota.CommandPalette" -Force -ErrorAction SilentlyContinue
    $manifestPath = "$PSScriptRoot\src\PowerQuota.CommandPalette\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\AppxManifest.xml"
    Add-AppxPackage -Register $manifestPath
}

Write-Host "==> Hot-reloading PowerToys Command Palette..." -ForegroundColor Green
Start-Process "x-cmdpal://reload"

Write-Host "==> Done! PowerQuota reloaded." -ForegroundColor Green
