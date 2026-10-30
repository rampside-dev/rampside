# Publishes the Rampside desktop app self-contained and builds the MSI from it:
# installer\bin\x64\Release\Rampside-<version>-x64.msi
# Used by build-installer.bat and by the GitHub Actions build, so both make the same installer.
param(
    # MSFS 2024 SDK folder (only SimConnect SDK\lib is needed). Default: MSFS2024_SDK, then C:\MSFS 2024 SDK.
    [string]$MsfsSdkRoot = $env:MSFS2024_SDK,
    # Version for the app and the MSI, e.g. 0.2.0. Empty: <Version> from Directory.Build.props.
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $PSScriptRoot 'obj\publish'

if (-not $MsfsSdkRoot) { $MsfsSdkRoot = 'C:\MSFS 2024 SDK' }
if (-not (Test-Path (Join-Path $MsfsSdkRoot 'SimConnect SDK\lib\SimConnect.dll'))) {
    throw "MSFS 2024 SDK not found in '$MsfsSdkRoot'. Set MSFS2024_SDK or pass -MsfsSdkRoot."
}
if ($Version -and $Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' must look like 1.2.3 (an MSI version has no suffix)."
}

$props = @("-p:MsfsSdkRoot=$MsfsSdkRoot")
if ($Version) { $props += "-p:Version=$Version" }

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host 'Publishing Rampside...'
dotnet publish (Join-Path $repo 'src\Rampside.Desktop') -c Release -r win-x64 --self-contained `
    -p:DebugType=none -o $publish -nologo -v q @props
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }

# --no-incremental: published files keep their old timestamps, so an incremental build could ship a stale MSI.
Write-Host 'Building the installer...'
dotnet build $PSScriptRoot -c Release --no-incremental -nologo -v q "-p:RampsideAppDir=$publish" @props
if ($LASTEXITCODE) { throw 'Building the installer failed.' }

Get-ChildItem (Join-Path $PSScriptRoot 'bin\x64\Release\*.msi') | ForEach-Object { Write-Host "Done: $($_.FullName)" }
