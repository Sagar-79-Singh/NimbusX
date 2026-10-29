<#
.SYNOPSIS
    Downloads the external binaries NimbusX pins and verifies their SHA-256.

.DESCRIPTION
    NimbusX never downloads anything at run time. This script is the only download step and it is run by a
    packager (or by a developer) before a build. Every file is checked against the digest compiled into
    src/NimbusX.Mount/DependencyVersions.cs; a mismatch deletes the download and fails the script.

    The same script is what a user runs when the app tells them "rclone is missing": it puts the verified
    binaries into %LOCALAPPDATA%\NimbusX\tools so the app can use them without any network access of its own.

.PARAMETER Destination
    Where the verified files are placed. Defaults to %LOCALAPPDATA%\NimbusX\tools.

.PARAMETER Only
    Restrict the download to one dependency: rclone, terabox or winfsp.

.PARAMETER SkipInstallers
    Skip WinFsp (an elevated MSI install is a separate, visible step the user runs themselves).

.EXAMPLE
    pwsh -File scripts/fetch-deps.ps1

.EXAMPLE
    pwsh -File scripts/fetch-deps.ps1 -Only rclone -Destination C:\build\payload
#>
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'NimbusX\tools'),
    [ValidateSet('all', 'rclone', 'terabox', 'winfsp')]
    [string]$Only = 'all',
    [switch]$SkipInstallers
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Keep these in sync with DependencyVersions.cs. A test (PinnedDigestTests) pins the format of the values there;
# this table is the packaging copy and is checked by scripts/verify-deps.ps1 as well.
$Dependencies = @(
    [pscustomobject]@{
        Key      = 'rclone'
        Name     = 'rclone 1.75.2 (Windows x64)'
        Url      = 'https://github.com/rclone/rclone/releases/download/v1.75.2/rclone-v1.75.2-windows-amd64.zip'
        Sha256   = '8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818'
        FileName = 'rclone-v1.75.2-windows-amd64.zip'
        Kind     = 'zip'
        Folder   = 'rclone'
    },
    [pscustomobject]@{
        Key      = 'terabox'
        Name     = 'rclone-extra 1.75.2-extra, TeraBox backend (Windows x64)'
        Url      = 'https://github.com/gulp79/rclone-extra/releases/download/v1.75.2-extra/rclone-windows-amd64.zip'
        Sha256   = 'd5a5d9383a607ccb1af9a871d4cb92906121f32cf111497adb94958b99315c69'
        FileName = 'rclone-windows-amd64.zip'
        Kind     = 'zip'
        Folder   = 'terabox'
    },
    [pscustomobject]@{
        Key      = 'winfsp'
        Name     = 'WinFsp 2.1.25156 installer'
        Url      = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
        Sha256   = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
        FileName = 'winfsp-2.1.25156.msi'
        Kind     = 'msi'
        Folder   = 'winfsp'
    }
)

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

function Get-VerifiedFile {
    param([pscustomobject]$Dependency, [string]$CacheDirectory)

    $target = Join-Path $CacheDirectory $Dependency.FileName
    if (Test-Path -LiteralPath $target) {
        $existing = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($existing -eq $Dependency.Sha256) {
            Write-Host "    cached and verified: $($Dependency.FileName)" -ForegroundColor Green
            return $target
        }

        Write-Warning "    cached file has the wrong hash, downloading again: $($Dependency.FileName)"
        Remove-Item -LiteralPath $target -Force
    }

    Write-Host "    downloading $($Dependency.Name)"
    $partial = "$target.partial"
    Invoke-WebRequest -Uri $Dependency.Url -OutFile $partial -UseBasicParsing -MaximumRedirection 5

    $hash = (Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $Dependency.Sha256) {
        Remove-Item -LiteralPath $partial -Force
        throw "SHA-256 mismatch for $($Dependency.FileName). Expected $($Dependency.Sha256), got $hash. The download was discarded."
    }

    Move-Item -LiteralPath $partial -Destination $target -Force
    Write-Host "    verified: $($Dependency.FileName)" -ForegroundColor Green
    return $target
}

function Expand-VerifiedZip {
    param([string]$ZipPath, [string]$TargetFolder)

    if (Test-Path -LiteralPath $TargetFolder) {
        # Only remove what this script owns; never a user folder.
        Remove-Item -LiteralPath $TargetFolder -Recurse -Force
    }

    New-Item -ItemType Directory -Path $TargetFolder -Force | Out-Null
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("nimbusx-unzip-" + [guid]::NewGuid().ToString('n'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null

    try {
        Expand-Archive -LiteralPath $ZipPath -DestinationPath $staging -Force
        # The release archives contain one top-level folder (rclone-v1.75.2-windows-amd64); flatten it.
        $executable = Get-ChildItem -LiteralPath $staging -Recurse -Filter 'rclone.exe' | Select-Object -First 1
        if (-not $executable) {
            throw "rclone.exe was not found inside $ZipPath."
        }

        Copy-Item -Path (Join-Path $executable.DirectoryName '*') -Destination $TargetFolder -Recurse -Force
        $copy = Join-Path $TargetFolder 'rclone.exe'
        if (-not (Test-Path -LiteralPath $copy)) {
            throw "Copying rclone.exe into $TargetFolder failed."
        }

        Write-Host "    extracted to $TargetFolder" -ForegroundColor Green
    }
    finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Step "NimbusX dependency fetch"
Write-Host "    destination: $Destination"

$cache = Join-Path $Destination '.downloads'
New-Item -ItemType Directory -Path $cache -Force | Out-Null

$selected = $Dependencies | Where-Object {
    $Only -eq 'all' -or $_.Key -eq $Only
}

foreach ($dependency in $selected) {
    if ($SkipInstallers -and $dependency.Kind -eq 'msi') {
        Write-Host "    skipping $($dependency.Name) (-SkipInstallers)" -ForegroundColor Yellow
        continue
    }

    Write-Step $dependency.Name
    $file = Get-VerifiedFile -Dependency $dependency -CacheDirectory $cache

    switch ($dependency.Kind) {
        'zip' {
            Expand-VerifiedZip -ZipPath $file -TargetFolder (Join-Path $Destination $dependency.Folder)
        }
        'msi' {
            # The installer expects the MSI under tools\winfsp\ (see installer/NimbusX.iss).
            $installerFolder = Join-Path $Destination $dependency.Folder
            New-Item -ItemType Directory -Path $installerFolder -Force | Out-Null
            $installerCopy = Join-Path $installerFolder $dependency.FileName
            Copy-Item -LiteralPath $file -Destination $installerCopy -Force
            Write-Host "    installer ready: $installerCopy" -ForegroundColor Green
            Write-Host "    WinFsp needs one elevated install. NimbusX will show this path when it is missing." -ForegroundColor Yellow
        }
        default {
            throw "Unknown dependency kind: $($dependency.Kind)"
        }
    }
}

Write-Step "Licences"
# The binaries above are redistributed with NimbusX, so their licence texts travel with them. They live in
# tools/licenses (fetched once from upstream and pinned in docs/VERIFIED_EXTERNALS.md) and are copied next to the
# binaries; the installer puts them in {app}\licenses.
$licenseSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools/licenses'
if (Test-Path -LiteralPath $licenseSource) {
    $licenseTarget = Join-Path $Destination 'licenses'
    New-Item -ItemType Directory -Path $licenseTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $licenseSource '*') -Destination $licenseTarget -Force
    Write-Host "    licence texts copied to $licenseTarget" -ForegroundColor Green
}
else {
    Write-Warning "    no licence texts found at $licenseSource; the installer will ship without them."
}

Write-Step "Done"
Write-Host "NimbusX can now use these binaries. Nothing else is downloaded at run time." -ForegroundColor Green
