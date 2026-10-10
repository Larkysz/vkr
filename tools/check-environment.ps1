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
    if ($sdks -and ($sdks -notmatch 'No SDKs were found')) { Write-Status 'PASS' '.NET SDK' ($sdks -join '; ') } else { Write-Status 'BLOCKED' '.NET SDK' 'No SDKs reported by dotnet --list-sdks' }
    $sdkMsbuild = $info | Select-String 'MSBuild version'
    if ($sdkMsbuild) { Write-Status 'PASS' 'MSBuild (via .NET SDK)' ($sdkMsbuild -join '; ') }
    if ($runtimes) { Write-Status 'PASS' '.NET runtimes' ($runtimes -join '; ') } else { Write-Status 'WARN' '.NET runtimes' 'No runtimes reported' }
}

$vswhereCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
    (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
)
$vswhere = $vswhereCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
$instances = @()
if ($vswhere) {
    $instances = @((& $vswhere -products '*' -format json -utf8 | ConvertFrom-Json))
    Write-Status 'PASS' 'vswhere' $vswhere
} elseif (Get-Command vswhere.exe) {
    $vswhere = (Get-Command vswhere.exe).Source
    $instances = @((& $vswhere -products '*' -format json -utf8 | ConvertFrom-Json))
    Write-Status 'PASS' 'vswhere' $vswhere
} else {
    Write-Status 'MISSING' 'vswhere' 'Visual Studio locator not found in PATH or standard installer directories'
}

$vsFound = $false
$clFound = $false
$msbuildFound = $false
foreach ($instance in $instances) {
    $vsFound = $true
    $vsPath = $instance.installationPath
    $version = $instance.installationVersion
    Write-Status 'PASS' 'Visual Studio' ("$($instance.displayName) $version [$vsPath]")
    $cl = Get-ChildItem (Join-Path $vsPath 'VC\Tools\MSVC') -Filter cl.exe -Recurse | Where-Object { $_.FullName -like '*\Hostx64\x64\cl.exe' } | Select-Object -First 1
    if ($cl) { $clFound = $true; Write-Status 'PASS' 'MSVC x64 compiler' $cl.FullName }
    $msbuild = Join-Path $vsPath 'MSBuild\Current\Bin\MSBuild.exe'
    if (Test-Path $msbuild) { $msbuildFound = $true; Write-Status 'PASS' 'MSBuild.exe' $msbuild }
}
if (-not $vsFound) {
    $vsRoots = @('C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') | Where-Object { Test-Path $_ }
    if ($vsRoots) { Write-Status 'WARN' 'Visual Studio roots' ($vsRoots -join ', ') } else { Write-Status 'MISSING' 'Visual Studio' 'No Visual Studio installation found' }
}
if (-not $clFound) {
    $clCommand = Get-Command cl.exe
    if ($clCommand) { $clFound = $true; Write-Status 'PASS' 'MSVC compiler on PATH' $clCommand.Source } else { Write-Status 'MISSING' 'MSVC x64 compiler' 'cl.exe not found in discovered Visual Studio instances' }
}
if (-not $msbuildFound) {
    $msbuildCommand = Get-Command MSBuild.exe
    if ($msbuildCommand) { $msbuildFound = $true; Write-Status 'PASS' 'MSBuild.exe on PATH' $msbuildCommand.Source } else { Write-Status 'MISSING' 'MSBuild.exe' 'MSBuild.exe not found in discovered Visual Studio instances' }
}

$kitRoots = @('C:\Program Files (x86)\Windows Kits\10', 'C:\Program Files\Windows Kits\10') | Where-Object { Test-Path $_ }
$kitRoot = $kitRoots | Select-Object -First 1
$sdkVersion = $null
$wdkVersion = $null
if ($kitRoot) {
    $sdkCandidates = Get-ChildItem (Join-Path $kitRoot 'Include') -Directory | Where-Object Name -Match '^10\.' | Sort-Object Name -Descending
    foreach ($candidate in $sdkCandidates) {
        if (Test-Path (Join-Path $candidate.FullName 'um\Windows.h')) { $sdkVersion = $candidate.Name; break }
    }
    if ($sdkVersion) { Write-Status 'PASS' 'Windows SDK headers' ("$sdkVersion [$kitRoot]") } else { Write-Status 'MISSING' 'Windows SDK headers' 'Windows.h not found under Windows Kits Include' }
    $wdkCandidates = Get-ChildItem (Join-Path $kitRoot 'Include') -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'km\fltKernel.h') } | Sort-Object Name -Descending
    foreach ($candidate in $wdkCandidates) {
        $version = $candidate.Name
        $build = Join-Path $kitRoot ("build\$version")
        $lib = Join-Path $kitRoot ("Lib\$version\km\x64\fltMgr.lib")
        if ((Test-Path (Join-Path $build 'WindowsDriver.KernelMode.props')) -and (Test-Path $lib)) { $wdkVersion = $version; break }
    }
    if ($wdkVersion) { Write-Status 'PASS' 'WDK minifilter headers/libraries/build targets' ("$wdkVersion [$kitRoot]") } else { Write-Status 'MISSING' 'WDK build components' 'fltKernel.h, fltMgr.lib or Windows Driver MSBuild targets missing' }
} else {
    Write-Status 'MISSING' 'Windows SDK/WDK' 'Windows Kits 10 root not found'
}

if (Get-Command cmake.exe) { Write-Status 'PASS' 'CMake' (Get-Command cmake.exe).Source } else { Write-Status 'OPTIONAL' 'CMake' 'Not required by current WDK vcxproj probe' }
