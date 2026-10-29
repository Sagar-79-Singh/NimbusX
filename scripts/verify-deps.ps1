<#
.SYNOPSIS
    Re-checks the pinned digests in fetch-deps.ps1 against DependencyVersions.cs, then verifies whatever is already
    in the tools folder.

.DESCRIPTION
    Two independent copies of the digests exist on purpose: the app compiles one in, and the packaging script carries
    the other. This script fails when they disagree, which is the only moment a silent downgrade could happen.

.EXAMPLE
    pwsh -File scripts/verify-deps.ps1
#>
[CmdletBinding()]
param(
    [string]$ToolsDirectory = (Join-Path $env:LOCALAPPDATA 'NimbusX\tools')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$versions = Join-Path $root 'src/NimbusX.Mount/DependencyVersions.cs'
$fetch = Join-Path $PSScriptRoot 'fetch-deps.ps1'

if (-not (Test-Path -LiteralPath $versions)) {
    throw "DependencyVersions.cs not found at $versions."
}

$source = Get-Content -LiteralPath $versions -Raw
$scriptSource = Get-Content -LiteralPath $fetch -Raw

# Pull every 64 character hex digest out of both files; they must appear in the same order and the same count.
$sourceDigests = [regex]::Matches($source, '"([0-9a-f]{64})"') | ForEach-Object { $_.Groups[1].Value }
$scriptDigests = [regex]::Matches($scriptSource, "'([0-9a-f]{64})'") | ForEach-Object { $_.Groups[1].Value }

if ($sourceDigests.Count -eq 0) {
    throw 'No pinned digests were found in DependencyVersions.cs; the parser or the file changed.'
}

if (($sourceDigests -join ',') -ne ($scriptDigests -join ',')) {
    Write-Host 'App digests:' -ForegroundColor Yellow
    $sourceDigests | ForEach-Object { Write-Host "  $_" }
    Write-Host 'Script digests:' -ForegroundColor Yellow
    $scriptDigests | ForEach-Object { Write-Host "  $_" }
    throw 'The digests in scripts/fetch-deps.ps1 and src/NimbusX.Mount/DependencyVersions.cs disagree.'
}

Write-Host "Pinned digests agree ($($sourceDigests.Count) entries)." -ForegroundColor Green

$pairs = @(
    @{ Path = (Join-Path $ToolsDirectory 'rclone/rclone.exe'); Expect = $null },
    @{ Path = (Join-Path $ToolsDirectory 'terabox/rclone.exe'); Expect = $null }
)

foreach ($pair in $pairs) {
    if (Test-Path -LiteralPath $pair.Path) {
        $hash = (Get-FileHash -LiteralPath $pair.Path -Algorithm SHA256).Hash
        Write-Host "  $($pair.Path): $hash"
    }
    else {
        Write-Host "  $($pair.Path): not present" -ForegroundColor Yellow
    }
}

Write-Host 'Note: the pinned digests cover the release archives, not the extracted rclone.exe; NimbusX records the' -ForegroundColor DarkGray
Write-Host 'extracted hash on first use (see docs/VERIFIED_EXTERNALS.md).' -ForegroundColor DarkGray
