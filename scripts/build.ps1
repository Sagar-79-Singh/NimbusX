<#
.SYNOPSIS
    Restores, builds, tests and publishes NimbusX.

.DESCRIPTION
    One command for a release build:
      1. fetch and verify the pinned dependencies (unless -SkipDependencies)
      2. build the solution in Release
      3. run the unit tests
      4. publish the desktop app and the CLI to an installer payload folder

.PARAMETER Version
    Version stamped into the build (default 1.0.0).

.PARAMETER Payload
    Where the published apps are collected (default artifacts/payload).

.EXAMPLE
    pwsh -File scripts/build.ps1 -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0',
    [string]$Payload = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/payload'),
    [switch]$SkipDependencies,
    [switch]$SkipTests,
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipDependencies) {
        Write-Host '==> Dependencies' -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'fetch-deps.ps1') -Destination (Join-Path $Payload 'tools')
        & (Join-Path $PSScriptRoot 'verify-deps.ps1') -ToolsDirectory (Join-Path $Payload 'tools')
    }

    Write-Host '==> Restore' -ForegroundColor Cyan
    dotnet restore NimbusX.sln
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    Write-Host '==> Build' -ForegroundColor Cyan
    dotnet build NimbusX.sln -c Release --no-restore -p:Version=$Version -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    if (-not $SkipTests) {
        Write-Host '==> Test' -ForegroundColor Cyan
        dotnet test tests/NimbusX.Tests/NimbusX.Tests.csproj -c Release --no-build --logger 'console;verbosity=normal'
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    }

    Write-Host '==> Publish desktop' -ForegroundColor Cyan
    $desktopOut = Join-Path $Payload 'app'
    dotnet publish src/NimbusX.Desktop/NimbusX.Desktop.csproj -c Release -r win-x64 --self-contained:$($SelfContained.IsPresent) `
        -p:Version=$Version -p:PublishDir=$desktopOut -o $desktopOut
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the desktop app failed.' }

    Write-Host '==> Publish CLI' -ForegroundColor Cyan
    $cliOut = Join-Path $Payload 'cli'
    dotnet publish src/NimbusX.Cli/NimbusX.Cli.csproj -c Release -r win-x64 --self-contained:$($SelfContained.IsPresent) `
        -p:Version=$Version -o $cliOut
    if ($LASTEXITCODE -ne 0) { throw 'Publishing the CLI failed.' }

    Write-Host "==> Payload ready: $Payload" -ForegroundColor Green
    Write-Host '    Next: installer/NimbusX.iss (Inno Setup) turns this folder into NimbusX-Setup.exe.' -ForegroundColor Green
}
finally {
    Pop-Location
}
