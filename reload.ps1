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
    Get-AppxPackage *PowerQuota* | Remove-AppxPackage -ErrorAction SilentlyContinue
    Stop-Process -Name "PowerQuota.CommandPalette" -Force -ErrorAction SilentlyContinue
    $manifestPath = "$PSScriptRoot\src\PowerQuota.CommandPalette\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\AppxManifest.xml"
    Add-AppxPackage -Register $manifestPath

    # Clean up any orphaned/stale PowerQuota dock bands from Command Palette settings.json
    try {
        $cmdPalSettingsPath = "$env:LOCALAPPDATA\Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json"
        if (Test-Path $cmdPalSettingsPath) {
            $rawSettings = [System.IO.File]::ReadAllText($cmdPalSettingsPath)
            $doc = [System.Text.Json.Nodes.JsonNode]::Parse($rawSettings)
            $dockSettings = $doc["DockSettings"]
            if ($dockSettings -ne $null) {
                $changed = $false
                foreach ($section in @("StartBands", "CenterBands", "EndBands")) {
                    $bandArr = $dockSettings[$section]?.AsArray()
                    if ($bandArr -ne $null) {
                        $toRemove = New-Object System.Collections.Generic.List[System.Text.Json.Nodes.JsonNode]
                        for ($i = 0; $i -lt $bandArr.Count; $i++) {
                            $item = $bandArr[$i]
                            $cid = $item["CommandId"]?.GetValue[string]()
                            if ($cid -and ($cid -match "PowerQuota-CommandPalette\d+")) {
                                $toRemove.Add($item)
                            }
                        }
                        if ($toRemove.Count -gt 0) {
                            foreach ($item in $toRemove) {
                                $bandArr.Remove($item) | Out-Null
                            }
                            $changed = $true
                        }
                    }
                }
                if ($changed) {
                    $opt = New-Object System.Text.Json.JsonSerializerOptions
                    $opt.WriteIndented = $true
                    [System.IO.File]::WriteAllText($cmdPalSettingsPath, $doc.ToJsonString($opt))
                }
            }
        }
    }
    catch { }
}

Write-Host "==> Hot-reloading PowerToys Command Palette..." -ForegroundColor Green
Start-Process "x-cmdpal://reload"

Write-Host "==> Done! PowerQuota reloaded." -ForegroundColor Green
