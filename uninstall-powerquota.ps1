<#
.SYNOPSIS
    Removes sideloaded/developer instances of PowerQuota (preserving Microsoft Store packages).

.DESCRIPTION
    Discovers and removes sideloaded/developer PowerQuota AppX/MSIX packages
    (Identity: PowerQuota.CommandPalette), while leaving official Microsoft Store
    installations (Identity: 39231EricJamesSoltys.PowerQuota) untouched.
    
    Can also clean stale sideloaded provider entries, pinned dock bands, and cache
    from PowerToys Command Palette.

.PARAMETER IncludeStore
    If specified, also removes Microsoft Store packages. Defaults to $false.

.PARAMETER ClearCache
    Cleans Command Palette cache, settings, and dock bands corresponding to sideloaded
    instances (or all instances if -IncludeStore is specified).

.PARAMETER ReloadCmdPal
    Triggers Command Palette to reload its extensions after uninstallation.

.PARAMETER WhatIf
    Shows what actions would be performed without making changes.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch]$IncludeStore,
    [switch]$ClearCache,
    [switch]$ReloadCmdPal
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host "  [OK] $msg" -ForegroundColor Green
}

function Write-Info([string]$msg) {
    Write-Host "  $msg" -ForegroundColor Gray
}

# 1. Check elevation
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Note: Running as current user. To remove packages provisioned for All Users, re-run in an elevated PowerShell prompt." -ForegroundColor Yellow
}

# 2. Terminate running sideloaded PowerQuota processes
Write-Step "Checking for running PowerQuota processes..."
$processes = Get-Process -Name "PowerQuota*", "*PowerQuota*" -ErrorAction SilentlyContinue
$procsToStop = @()

foreach ($proc in $processes) {
    $procPath = try { $proc.MainModule.FileName } catch { "" }
    $isStoreProc = ($procPath -like "*39231EricJamesSoltys*")
    
    if (-not $isStoreProc -or $IncludeStore) {
        $procsToStop += $proc
    } else {
        Write-Info "Preserving active Microsoft Store process '$($proc.ProcessName)' (PID $($proc.Id)) from $procPath"
    }
}

if ($procsToStop.Count -gt 0) {
    foreach ($proc in $procsToStop) {
        if ($PSCmdlet.ShouldProcess("Process '$($proc.ProcessName)' (PID $($proc.Id))", "Stop process")) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
            Write-Success "Stopped sideloaded process $($proc.ProcessName) (PID: $($proc.Id))"
        }
    }
} else {
    Write-Info "No sideloaded PowerQuota processes to terminate."
}

# 3. Discover installed AppX / MSIX packages
Write-Step "Searching for installed PowerQuota packages..."
$packageTypes = @("Main", "Bundle", "Resource", "Optional")
$packagesFound = [System.Collections.Generic.List[PSObject]]::new()
$seenFullNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($type in $packageTypes) {
    $found = @()
    try {
        $found = Get-AppxPackage *PowerQuota* -PackageTypeFilter $type -ErrorAction SilentlyContinue
    } catch {
        $found = Get-AppxPackage *PowerQuota* -ErrorAction SilentlyContinue
    }
    
    foreach ($pkg in $found) {
        if ($pkg -and $seenFullNames.Add($pkg.PackageFullName)) {
            $packagesFound.Add($pkg)
        }
    }
}

$devPackages = Get-AppxPackage *PowerQuota.CommandPalette* -ErrorAction SilentlyContinue
foreach ($pkg in $devPackages) {
    if ($pkg -and $seenFullNames.Add($pkg.PackageFullName)) {
        $packagesFound.Add($pkg)
    }
}

# Filter by sideloaded vs Store
$sideloadedPackages = @()
$storePackages = @()

foreach ($pkg in $packagesFound) {
    $isStore = ($pkg.SignatureKind -eq "Store" -or $pkg.PackageFamilyName -like "*39231EricJamesSoltys*")
    if ($isStore) {
        $storePackages += $pkg
    } else {
        $sideloadedPackages += $pkg
    }
}

if ($storePackages.Count -gt 0) {
    Write-Host "Detected $($storePackages.Count) Microsoft Store package(s) (preserved):" -ForegroundColor DarkGray
    foreach ($pkg in $storePackages) {
        $kind = if ($pkg.IsBundle) { "Bundle" } else { "Main" }
        Write-Host "  - [$kind] $($pkg.Name) (v$($pkg.Version))" -ForegroundColor DarkGray
    }
}

$packagesToRemove = if ($IncludeStore) { $packagesFound } else { $sideloadedPackages }

if ($packagesToRemove.Count -eq 0) {
    Write-Info "No sideloaded PowerQuota packages are currently installed."
} else {
    $sortedPackages = @($packagesToRemove | Sort-Object -Property @{ Expression = { if ($_.IsBundle) { 0 } else { 1 } } })
    Write-Host "Found $($sortedPackages.Count) sideloaded package(s) to remove:" -ForegroundColor White
    foreach ($pkg in $sortedPackages) {
        $kind = if ($pkg.IsBundle) { "Bundle" } else { "Main" }
        Write-Host "  - [$kind] $($pkg.Name) (v$($pkg.Version)) - $($pkg.PackageFullName)" -ForegroundColor White
    }

    # 4. Remove packages
    Write-Step "Removing packages..."
    foreach ($pkg in $sortedPackages) {
        if ($PSCmdlet.ShouldProcess($pkg.PackageFullName, "Remove AppX package")) {
            try {
                if ($isAdmin) {
                    Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers -ErrorAction Stop
                } else {
                    Remove-AppxPackage -Package $pkg.PackageFullName -ErrorAction Stop
                }
                Write-Success "Removed package: $($pkg.PackageFullName)"
            } catch {
                $errMsg = $_.Exception.Message
                if ($errMsg -like "*not found*" -or $errMsg -like "*0x80073CF1*") {
                    Write-Info "Package already removed (e.g. as part of bundle): $($pkg.PackageFullName)"
                } else {
                    Write-Warning "Failed to remove $($pkg.PackageFullName): $errMsg"
                }
            }
        }
    }
}

# 5. Clean Command Palette cache and dock settings (if requested)
if ($ClearCache) {
    Write-Step "Cleaning Command Palette cache and settings..."

    # Helper scriptblock to identify target providers
    $isTargetProvider = {
        param([string]$provId)
        if (-not $provId) { return $false }
        if ($IncludeStore) {
            return ($provId -like "*PowerQuota*" -or $provId -like "*powerquota*")
        } else {
            # Target sideloaded provider (PowerQuota.CommandPalette_h5agxdcs07ec6...), preserve Store (39231EricJamesSoltys...)
            return ($provId -like "*PowerQuota.CommandPalette*" -and $provId -notlike "*39231EricJamesSoltys*")
        }
    }

    # Clean commandProviderCache.json
    $cmdPalCachePath = "$env:LOCALAPPDATA\Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\commandProviderCache.json"
    if (Test-Path $cmdPalCachePath) {
        try {
            $cacheJson = Get-Content $cmdPalCachePath -Raw | ConvertFrom-Json -AsHashtable
            if ($cacheJson.ContainsKey("Cache")) {
                $keysToRemove = @($cacheJson["Cache"].Keys | Where-Object { & $isTargetProvider $_ })
                if ($keysToRemove.Count -gt 0) {
                    foreach ($k in $keysToRemove) {
                        if ($PSCmdlet.ShouldProcess($cmdPalCachePath, "Remove cached provider '$k'")) {
                            $cacheJson["Cache"].Remove($k)
                            Write-Info "Removed cached provider: $k"
                        }
                    }
                    if ($PSCmdlet.ShouldProcess($cmdPalCachePath, "Save updated commandProviderCache.json")) {
                        $cacheJson | ConvertTo-Json -Depth 10 | Set-Content $cmdPalCachePath -Encoding UTF8
                        Write-Success "Updated commandProviderCache.json"
                    }
                } else {
                    Write-Info "No stale providers in commandProviderCache.json."
                }
            }
        } catch {
            Write-Warning "Could not update $($cmdPalCachePath): $_"
        }
    }

    # Clean settings.json ProviderSettings and Dock pinned bands
    $cmdPalSettingsPath = "$env:LOCALAPPDATA\Packages\Microsoft.CommandPalette_8wekyb3d8bbwe\LocalState\settings.json"
    if (Test-Path $cmdPalSettingsPath) {
        try {
            $settingsJson = Get-Content $cmdPalSettingsPath -Raw | ConvertFrom-Json -AsHashtable
            $dirty = $false

            if ($settingsJson.ContainsKey("ProviderSettings")) {
                $provKeys = @($settingsJson["ProviderSettings"].Keys | Where-Object { & $isTargetProvider $_ })
                if ($provKeys.Count -gt 0) {
                    $dirty = $true
                    foreach ($k in $provKeys) {
                        if ($PSCmdlet.ShouldProcess($cmdPalSettingsPath, "Remove ProviderSettings entry '$k'")) {
                            $settingsJson["ProviderSettings"].Remove($k)
                            Write-Info "Removed ProviderSettings: $k"
                        }
                    }
                }
            }

            if ($settingsJson.ContainsKey("DockSettings")) {
                foreach ($bandKey in @("StartBands", "CenterBands", "EndBands")) {
                    if ($settingsJson["DockSettings"].ContainsKey($bandKey) -and $settingsJson["DockSettings"][$bandKey]) {
                        $original = @($settingsJson["DockSettings"][$bandKey])
                        $filtered = @($original | Where-Object { -not (& $isTargetProvider $_.ProviderId) })
                        if ($filtered.Count -ne $original.Count) {
                            $dirty = $true
                            if ($PSCmdlet.ShouldProcess($cmdPalSettingsPath, "Remove $($original.Count - $filtered.Count) dock band(s) from $bandKey")) {
                                $settingsJson["DockSettings"][$bandKey] = $filtered
                                Write-Info "Removed $($original.Count - $filtered.Count) sideloaded band(s) from $bandKey"
                            }
                        }
                    }
                }
            }

            if ($dirty) {
                if ($PSCmdlet.ShouldProcess($cmdPalSettingsPath, "Save updated settings.json")) {
                    $settingsJson | ConvertTo-Json -Depth 10 | Set-Content $cmdPalSettingsPath -Encoding UTF8
                    Write-Success "Cleaned sideloaded PowerQuota entries from Command Palette settings.json"
                }
            } else {
                Write-Info "No stale sideloaded entries found in settings.json."
            }
        } catch {
            Write-Warning "Could not update $($cmdPalSettingsPath): $_"
        }
    }

    # Clean local logs/data only if IncludeStore is specified
    if ($IncludeStore) {
        $localDataDir = "$env:LOCALAPPDATA\PowerQuota"
        if (Test-Path $localDataDir) {
            if ($PSCmdlet.ShouldProcess($localDataDir, "Remove PowerQuota local AppData folder")) {
                Remove-Item -Path $localDataDir -Recurse -Force -ErrorAction SilentlyContinue
                Write-Success "Removed $localDataDir"
            }
        }
    }
}

# 6. Reload Command Palette (if requested)
if ($ReloadCmdPal) {
    Write-Step "Reloading Command Palette..."
    if ($PSCmdlet.ShouldProcess("Command Palette", "Reload extensions")) {
        Start-Process "x-cmdpal://reload"
        Write-Success "Sent reload command to Command Palette."
    }
}

Write-Step "Done!"
