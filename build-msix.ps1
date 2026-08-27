[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version,
    [string]$OutputDir = "$PSScriptRoot\artifacts",
    [switch]$ForStore,
    [switch]$Bundle,
    [string]$PackageIdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [string]$CertificatePath,
    [string]$CertificatePassword = "Password123!",
    [switch]$SelfContained,
    [switch]$Install
)

$ErrorActionPreference = "Stop"

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

function Write-Success([string]$msg) {
    Write-Host "==> $msg" -ForegroundColor Green
}

function Find-WindowsSdkTool([string]$toolName) {
    $sdkRoots = @(
        "C:\Program Files (x86)\Windows Kits\10\bin",
        "C:\Program Files\Windows Kits\10\bin"
    )
    foreach ($root in $sdkRoots) {
        if (Test-Path $root) {
            $tool = Get-ChildItem -Path $root -Recurse -Filter $toolName -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match "\\x64\\" } |
                Sort-Object FullName -Descending |
                Select-Object -First 1
            if ($tool) { return $tool.FullName }
        }
    }
    return $null
}

# Resolve Version dynamically from Package.appxmanifest if not explicitly passed
if ([string]::IsNullOrWhiteSpace($Version)) {
    $sourceManifest = Join-Path $PSScriptRoot "src\PowerQuota.CommandPalette\Package.appxmanifest"
    if (Test-Path $sourceManifest) {
        [xml]$srcXml = Get-Content $sourceManifest
        $Version = $srcXml.Package.Identity.Version
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "1.0.0.0"
    }
}

# Resolve PackageIdentityName from environment variable if not specified
if ([string]::IsNullOrWhiteSpace($PackageIdentityName) -and -not [string]::IsNullOrWhiteSpace($env:PackageIdentityName)) {
    $PackageIdentityName = "$($env:PackageIdentityName).PowerQuota"
}

# Resolve Publisher from environment variable if not specified
if ([string]::IsNullOrWhiteSpace($Publisher) -and -not [string]::IsNullOrWhiteSpace($env:PackageIdentityPublisher)) {
    if ($env:PackageIdentityPublisher.StartsWith("CN=", [System.StringComparison]::OrdinalIgnoreCase)) {
        $Publisher = $env:PackageIdentityPublisher
    } else {
        $Publisher = "CN=$($env:PackageIdentityPublisher)"
    }
}

# Resolve PublisherDisplayName from environment variable if not specified
if ([string]::IsNullOrWhiteSpace($PublisherDisplayName) -and -not [string]::IsNullOrWhiteSpace($env:PublisherDisplayName)) {
    $PublisherDisplayName = $env:PublisherDisplayName
}

Write-Step "Checking build environment and prerequisites..."
Write-Host "Resolved Configuration:" -ForegroundColor DarkGray
Write-Host "  Version             : $Version" -ForegroundColor DarkGray
if ($PackageIdentityName) { Write-Host "  PackageIdentityName : $PackageIdentityName" -ForegroundColor DarkGray }
if ($Publisher) { Write-Host "  Publisher           : $Publisher" -ForegroundColor DarkGray }
if ($PublisherDisplayName) { Write-Host "  PublisherDisplayName: $PublisherDisplayName" -ForegroundColor DarkGray }

$makeAppx = Find-WindowsSdkTool "makeappx.exe"
if (-not $makeAppx) {
    Write-Error "Could not locate makeappx.exe in Windows Kits directory. Please ensure the Windows 10/11 SDK is installed."
    exit 1
}

$signtool = Find-WindowsSdkTool "signtool.exe"
if (-not $signtool -and -not $ForStore) {
    Write-Warning "Could not locate signtool.exe. Package will not be signed."
}

$dotnetPath = "dotnet"
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    $dotnetPath = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
}

# Resolve OutputDir
$resolvedOutputDir = if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir
} else {
    Join-Path $PSScriptRoot $OutputDir
}

if (-not (Test-Path $resolvedOutputDir)) {
    New-Item -ItemType Directory -Path $resolvedOutputDir -Force | Out-Null
}

function Build-ArchitecturePackage([string]$targetRuntime) {
    $arch = ($targetRuntime -split "-")[-1]
    $stagingDir = Join-Path $resolvedOutputDir "staging_$arch"
    if (Test-Path $stagingDir) {
        Remove-Item -Path $stagingDir -Recurse -Force
    }

    Write-Step "Publishing PowerQuota.CommandPalette ($Configuration, $targetRuntime)..."
    $selfContainedArg = if ($SelfContained) { "--self-contained true" } else { "--self-contained false" }
    & $dotnetPath publish "$PSScriptRoot\src\PowerQuota.CommandPalette\PowerQuota.CommandPalette.csproj" -c $Configuration -r $targetRuntime $selfContainedArg.Split(" ") -o "$stagingDir" | Out-Host

    if ($LASTEXITCODE -ne 0) {
        Write-Error "dotnet publish failed for $targetRuntime with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Update manifest if overrides or environment variables provided
    $manifestPath = Join-Path $stagingDir "AppxManifest.xml"
    if (Test-Path $manifestPath) {
        [xml]$manifest = Get-Content $manifestPath
        $modified = $false

        if ($Version) {
            $manifest.Package.Identity.Version = $Version
            $modified = $true
        }
        if ($PackageIdentityName) {
            $manifest.Package.Identity.Name = $PackageIdentityName
            $modified = $true
        }
        if ($Publisher) {
            $manifest.Package.Identity.Publisher = $Publisher
            $modified = $true
        }
        if ($PublisherDisplayName) {
            $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
            $modified = $true
        }
        if ($arch -eq "arm64") {
            $manifest.Package.Identity.ProcessorArchitecture = "arm64"
            $modified = $true
        } elseif ($arch -eq "x64") {
            $manifest.Package.Identity.ProcessorArchitecture = "x64"
            $modified = $true
        }

        if ($modified) {
            $manifest.Save($manifestPath)
        }
    }

    $packageFileName = if ($ForStore) {
        "PowerQuota_${Version}_${arch}_Store.msix"
    } else {
        "PowerQuota_${Version}_${arch}.msix"
    }
    $msixPath = Join-Path $resolvedOutputDir $packageFileName

    if (Test-Path $msixPath) {
        Remove-Item -Path $msixPath -Force
    }

    Write-Step "Packaging MSIX with MakeAppx ($packageFileName)..."
    & $makeAppx pack /d "$stagingDir" /p "$msixPath" /o /v | Out-Host

    if ($LASTEXITCODE -ne 0) {
        Write-Error "MakeAppx pack failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    # Clean staging directory
    if (Test-Path $stagingDir) {
        Remove-Item -Path $stagingDir -Recurse -Force
    }

    return $msixPath
}

if ($Bundle) {
    Write-Step "Building multi-architecture bundle (x64 + arm64)..."
    $x64Pkg = Build-ArchitecturePackage "win-x64"
    $arm64Pkg = Build-ArchitecturePackage "win-arm64"

    $bundleStagingDir = Join-Path $resolvedOutputDir "bundle_staging"
    if (Test-Path $bundleStagingDir) {
        Remove-Item -Path $bundleStagingDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path $bundleStagingDir -Force | Out-Null

    $x64Leaf = if ($ForStore) { "PowerQuota_${Version}_x64_Store.msix" } else { "PowerQuota_${Version}_x64.msix" }
    $arm64Leaf = if ($ForStore) { "PowerQuota_${Version}_arm64_Store.msix" } else { "PowerQuota_${Version}_arm64.msix" }

    Copy-Item (Join-Path $resolvedOutputDir $x64Leaf) -Destination (Join-Path $bundleStagingDir $x64Leaf)
    Copy-Item (Join-Path $resolvedOutputDir $arm64Leaf) -Destination (Join-Path $bundleStagingDir $arm64Leaf)

    $bundleFileName = if ($ForStore) {
        "PowerQuota_${Version}_Store.msixbundle"
    } else {
        "PowerQuota_${Version}.msixbundle"
    }
    $bundlePath = Join-Path $resolvedOutputDir $bundleFileName

    if (Test-Path $bundlePath) {
        Remove-Item -Path $bundlePath -Force
    }

    Write-Step "Creating MSIX Bundle with MakeAppx ($bundleFileName)..."
    & $makeAppx bundle /d "$bundleStagingDir" /p "$bundlePath" /bv "$Version" /o /v | Out-Host

    if ($LASTEXITCODE -ne 0) {
        Write-Error "MakeAppx bundle failed with exit code $LASTEXITCODE"
        exit $LASTEXITCODE
    }

    Remove-Item -Path $bundleStagingDir -Recurse -Force -ErrorAction SilentlyContinue

    Write-Success "MSIX Bundle Built Successfully!"
    Write-Host " Output File: $bundlePath" -ForegroundColor White
    $fileInfo = Get-Item $bundlePath
    $sizeMB = [Math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host " Bundle Size: $sizeMB MB" -ForegroundColor White
} else {
    $targetArch = ($Runtime -split "-")[-1]
    $pkgLeaf = if ($ForStore) {
        "PowerQuota_${Version}_${targetArch}_Store.msix"
    } else {
        "PowerQuota_${Version}_${targetArch}.msix"
    }
    $singlePackage = Build-ArchitecturePackage $Runtime
    $singlePackagePath = Join-Path $resolvedOutputDir $pkgLeaf

    # Sign package if not building for Store
    if (-not $ForStore -and $signtool) {
        Write-Step "Signing MSIX package for local testing and sideloading..."

        $certFile = $CertificatePath
        if (-not $certFile -or -not (Test-Path $certFile)) {
            $certFile = Join-Path $resolvedOutputDir "PowerQuotaDevCert.pfx"
            if (-not (Test-Path $certFile)) {
                $publisherCN = if ($Publisher) { $Publisher } else { "CN=PowerQuota" }
                Write-Host "Creating local self-signed developer certificate ($publisherCN)..." -ForegroundColor Yellow
                $securePwd = ConvertTo-SecureString $CertificatePassword -AsPlainText -Force
                $cert = New-SelfSignedCertificate -Type Custom `
                    -Subject $publisherCN `
                    -KeyUsage DigitalSignature `
                    -FriendlyName "PowerQuota Dev Certificate" `
                    -CertStoreLocation "Cert:\CurrentUser\My" `
                    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")

                Export-PfxCertificate -Cert $cert -FilePath $certFile -Password $securePwd | Out-Null
                
                try {
                    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "CurrentUser")
                    $store.Open("ReadWrite")
                    $store.Add($cert)
                    $store.Close()
                    Write-Host "Imported dev certificate to CurrentUser\TrustedPeople store." -ForegroundColor Gray
                } catch {
                    Write-Warning "Could not automatically import certificate to TrustedPeople: $_"
                }
            }
        }

        Write-Host "Signing $pkgLeaf with signtool..." -ForegroundColor Gray
        & $signtool sign /fd SHA256 /a /f "$certFile" /p "$CertificatePassword" "$singlePackagePath" | Out-Host
        if ($LASTEXITCODE -eq 0) {
            Write-Success "MSIX signed successfully."
        } else {
            Write-Warning "signtool failed to sign the MSIX package (Exit code $LASTEXITCODE)."
        }
    } elseif ($ForStore) {
        Write-Host "Skipping signing: Store submissions will be signed by Microsoft Partner Center." -ForegroundColor Yellow
    }

    $fileInfo = Get-Item $singlePackagePath
    $sizeMB = [Math]::Round($fileInfo.Length / 1MB, 2)

    Write-Success "MSIX Build Completed Successfully!"
    Write-Host "--------------------------------------------------"
    Write-Host " Output File : $singlePackagePath" -ForegroundColor White
    Write-Host " Package Size: $sizeMB MB" -ForegroundColor White
    Write-Host " Version     : $Version" -ForegroundColor White
    Write-Host "--------------------------------------------------"

    if ($Install) {
        Write-Step "Installing/Updating MSIX package on local system..."
        Stop-Process -Name "PowerQuota.CommandPalette" -Force -ErrorAction SilentlyContinue
        Add-AppxPackage -Path $singlePackagePath -ForceUpdateFromAnyVersion
        Write-Success "PowerQuota MSIX installed successfully!"
        Start-Process "x-cmdpal://reload"
    }
}
