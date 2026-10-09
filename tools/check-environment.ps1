[CmdletBinding()]
param()

$ErrorActionPreference = 'SilentlyContinue'

function Write-Status([string]$State, [string]$Name, [string]$Detail) {
    Write-Output ("{0,-7} {1}: {2}" -f $State, $Name, $Detail)
}

$dotnet = Get-Command dotnet
if ($dotnet) { Write-Status 'PASS' 'dotnet' $dotnet.Source } else { Write-Status 'BLOCKED' 'dotnet' 'dotnet.exe not found' }

if ($dotnet) {
    $sdks = dotnet --list-sdks 2>&1
    $info = dotnet --info 2>&1
    $runtimes = dotnet --list-runtimes 2>&1
    if ($sdks -and ($sdks -notmatch 'No SDKs were found')) { Write-Status 'PASS' '.NET SDK' (($sdks -join '; ')) } else { Write-Status 'BLOCKED' '.NET SDK' 'No SDKs reported by dotnet --list-sdks' }
    $sdkMsbuild = $info | Select-String 'MSBuild version'
    if ($sdkMsbuild) { Write-Status 'PASS' 'MSBuild (via .NET SDK)' (($sdkMsbuild -join '; ')) }
    if ($runtimes) { Write-Status 'PASS' '.NET runtimes' (($runtimes -join '; ')) } else { Write-Status 'WARN' '.NET runtimes' 'No runtimes reported' }
}

foreach ($tool in @('msbuild', 'MSBuild.exe', 'cl', 'cmake', 'vswhere')) {
    $command = Get-Command $tool
    if ($command) { Write-Status 'PASS' $tool $command.Source } else { Write-Status 'MISSING' $tool 'Command not found on PATH' }
}

$vsRoots = @(
    'C:\Program Files\Microsoft Visual Studio',
    'C:\Program Files (x86)\Microsoft Visual Studio'
)
$vsFound = $vsRoots | Where-Object { Test-Path $_ }
if ($vsFound) { Write-Status 'PASS' 'Visual Studio/Build Tools roots' ($vsFound -join ', ') }
else { Write-Status 'MISSING' 'Visual Studio/Build Tools roots' 'Known installation roots not found' }

$kitRoots = @('C:\Program Files (x86)\Windows Kits', 'C:\Program Files\Windows Kits')
$kitFound = $false
foreach ($root in $kitRoots) {
    if (Test-Path $root) {
        $kitFound = $true
        $versions = Get-ChildItem $root -Directory | Select-Object -ExpandProperty Name
        Write-Status 'PASS' 'Windows SDK root' ("$root; versions: " + ($versions -join ', '))
    }
}
if (-not $kitFound) { Write-Status 'MISSING' 'Windows SDK' 'Windows Kits directories not found' }

$wdkMarkers = @(
    'C:\Program Files (x86)\Windows Kits\10\Include',
    'C:\Program Files (x86)\Windows Kits\10\Tools',
    'C:\Program Files\Windows Kits\10\Include'
)
$wdkFound = $wdkMarkers | Where-Object { Test-Path $_ }
if ($wdkFound) { Write-Status 'PASS' 'WDK/Windows Kits markers' ($wdkFound -join ', ') } else { Write-Status 'MISSING' 'WDK' 'No WDK/Windows Kits include or tools marker found' }

