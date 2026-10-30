# Gets the SimConnect DLLs for a CI build from Microsoft's MSFS 2024 SDK installer.
# The DLLs are not in this repository (MSFS SDK EULA); they only ship inside the Rampside installer.
# Result: <OutDir>\SimConnect SDK\lib\SimConnect.dll and ...\lib\managed\Microsoft.FlightSimulator.SimConnect.dll,
# the layout Directory.Build.props expects for MsfsSdkRoot.
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Sha256,
    [Parameter(Mandatory)] [string]$OutDir,
    # Already downloaded SDK zip; empty = download it.
    [string]$ZipPath
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # the progress bar makes Invoke-WebRequest very slow
$work = Join-Path ([IO.Path]::GetTempPath()) "msfs-sdk-$Version"
New-Item -ItemType Directory -Force $work | Out-Null

if (-not $ZipPath) {
    $ZipPath = Join-Path $work 'sdk.zip'
    $url = "https://sdk.flightsimulator.com/msfs2024/files/installers/$Version/MSFS2024_SDK_Core_Installer_$Version.zip"
    Write-Host "Downloading $url"
    Invoke-WebRequest $url -OutFile $ZipPath
}

$actual = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
if ($actual -ne $Sha256) {
    throw "SDK zip checksum is $actual, expected $Sha256. Did Microsoft replace the file? Check it and update MSFS_SDK_SHA256."
}

Write-Host 'Unpacking the SDK installer'
$installer = Join-Path $work 'installer'
Expand-Archive $ZipPath $installer -Force
$msi = Get-ChildItem $installer -Recurse -Filter '*.msi' | Select-Object -First 1

# An administrative install only unpacks the files; nothing is installed on the machine.
$extract = Join-Path $work 'extract'
$log = Join-Path $work 'msiexec.log'
$p = Start-Process msiexec -ArgumentList "/a `"$($msi.FullName)`" /qn TARGETDIR=`"$extract`" /l*v `"$log`"" -Wait -PassThru
if ($p.ExitCode -ne 0) { Get-Content $log -Tail 30; throw "msiexec /a failed with $($p.ExitCode)." }

$native = Get-ChildItem $extract -Recurse -Filter 'SimConnect.dll' | Where-Object { $_.Directory.Name -eq 'lib' } | Select-Object -First 1
$managed = Get-ChildItem $extract -Recurse -Filter 'Microsoft.FlightSimulator.SimConnect.dll' | Select-Object -First 1
if (-not $native -or -not $managed) { throw 'SimConnect DLLs not found in the SDK installer.' }

$lib = Join-Path $OutDir 'SimConnect SDK\lib'
New-Item -ItemType Directory -Force (Join-Path $lib 'managed') | Out-Null
Copy-Item $native.FullName (Join-Path $lib 'SimConnect.dll')
Copy-Item $managed.FullName (Join-Path $lib 'managed\Microsoft.FlightSimulator.SimConnect.dll')
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

Get-ChildItem $lib -Recurse -File | ForEach-Object { Write-Host "$($_.FullName)  $($_.Length) bytes" }
