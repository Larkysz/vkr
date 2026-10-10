param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Root
)

$ErrorActionPreference = 'Stop'

$rootPath = [System.IO.Path]::GetFullPath($Root)
if ($rootPath.StartsWith('\\', [System.StringComparison]::Ordinal)) {
    throw 'Root must be on a local drive.'
}
if (-not [System.IO.Directory]::Exists($rootPath)) {
    throw 'Root must be an existing directory on an isolated test Windows.'
}

$rootItem = Get-Item -LiteralPath $rootPath -Force
if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'Root must not be a reparse point.'
}

$driveRoot = [System.IO.Path]::GetPathRoot($rootPath)
if ($rootPath.TrimEnd('\') -eq $driveRoot.TrimEnd('\')) {
    throw 'Root must be a dedicated directory, not a drive root.'
}
$drive = [System.IO.DriveInfo]::new($driveRoot)
if ($drive.DriveType -ne [System.IO.DriveType]::Fixed -or $drive.DriveFormat -ne 'NTFS') {
    throw 'Root must be on a fixed local NTFS volume.'
}

$workspaceName = 'TransactionalWindows-FSProbe-' + [guid]::NewGuid().ToString('N')
$workspace = Join-Path $rootPath $workspaceName
$created = $false

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

try {
    [void][System.IO.Directory]::CreateDirectory($workspace)
    $created = $true

    $childDirectory = Join-Path $workspace 'child'
    [void][System.IO.Directory]::CreateDirectory($childDirectory)
    Assert-True ([System.IO.Directory]::Exists($childDirectory)) 'Directory creation failed.'
    Write-Output '[PASS] create directory'

    $oldPath = Join-Path $childDirectory 'before.txt'
    $newPath = Join-Path $childDirectory 'after.txt'
    [System.IO.File]::WriteAllText($oldPath, 'before')
    Assert-True ([System.IO.File]::ReadAllText($oldPath) -eq 'before') 'Initial file read failed.'
    Write-Output '[PASS] create and read file'

    [System.IO.File]::WriteAllText($oldPath, 'after-write')
    Assert-True ([System.IO.File]::ReadAllText($oldPath) -eq 'after-write') 'File overwrite failed.'
    Write-Output '[PASS] overwrite file'

    [System.IO.File]::Move($oldPath, $newPath)
    Assert-True (-not [System.IO.File]::Exists($oldPath)) 'Rename left the old name visible.'
    Assert-True ([System.IO.File]::ReadAllText($newPath) -eq 'after-write') 'Renamed file content differs.'
    Write-Output '[PASS] rename file'

    $names = @([System.IO.Directory]::GetFileSystemEntries($childDirectory) | ForEach-Object { [System.IO.Path]::GetFileName($_) })
    if (($names.Count -ne 1) -or ($names[0] -ne 'after.txt')) {
        throw ('Directory enumeration returned unexpected entries: ' + ($names -join ', '))
    }
    Write-Output '[PASS] enumerate directory'

    [System.IO.File]::Delete($newPath)
    Assert-True (-not [System.IO.File]::Exists($newPath)) 'File deletion failed.'
    [System.IO.Directory]::Delete($childDirectory)
    Assert-True (-not [System.IO.Directory]::Exists($childDirectory)) 'Directory deletion failed.'
    Write-Output '[PASS] delete file and directory'
}
finally {
    if ($created -and [System.IO.Directory]::Exists($workspace)) {
        $actualWorkspace = [System.IO.Path]::GetFullPath((Get-Item -LiteralPath $workspace -Force).FullName)
        $actualParent = [System.IO.Path]::GetDirectoryName($actualWorkspace)
        $actualLeaf = [System.IO.Path]::GetFileName($actualWorkspace)
        if (-not $actualParent.Equals([System.IO.Path]::GetFullPath($rootPath), [System.StringComparison]::OrdinalIgnoreCase) -or
            -not $actualLeaf.StartsWith('TransactionalWindows-FSProbe-', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup because the scratch path is outside the expected location.'
        }

        $reparsePoints = Get-ChildItem -LiteralPath $workspace -Force -Recurse | Where-Object {
            ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
        }
        if ($reparsePoints) {
            Write-Warning "Scratch directory retained because it contains a reparse point: $workspace"
        }
        else {
            Remove-Item -LiteralPath $workspace -Recurse -Force
        }
    }
}
