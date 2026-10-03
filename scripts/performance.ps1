param(
    [string]$SnapshotFile,
    [string]$StateFile,
    [string]$OutputFile = 'artifacts/verification/row-projection-performance.json'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Resolve-ProjectSdk
$project = Join-Path $repository 'tests/SimpleScraper.Performance/SimpleScraper.Performance.csproj'
$outputPath = if ([IO.Path]::IsPathRooted($OutputFile)) { [IO.Path]::GetFullPath($OutputFile) } else { [IO.Path]::GetFullPath((Join-Path $repository $OutputFile)) }
$artifactsPath = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$activeAppPath = [IO.Path]::GetFullPath((Join-Path $artifactsPath 'app'))
if (!$outputPath.StartsWith($artifactsPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $outputPath.Equals($activeAppPath, [StringComparison]::OrdinalIgnoreCase) -or $outputPath.StartsWith($activeAppPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputFile must be inside repository artifacts and outside artifacts/app.'
}
$ancestor = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($outputPath))
while ($ancestor -and $ancestor.FullName.StartsWith($artifactsPath, [StringComparison]::OrdinalIgnoreCase)) {
    if ($ancestor.Exists -and (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'OutputFile ancestors cannot be symbolic links or junctions.' }
    $ancestor = $ancestor.Parent
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
$arguments = @('--output', $outputPath)
if ($SnapshotFile -or $StateFile) {
    if (!$SnapshotFile -or !$StateFile) { throw 'SnapshotFile and StateFile must be provided together.' }
    if ($outputPath.Equals([IO.Path]::GetFullPath($SnapshotFile), [StringComparison]::OrdinalIgnoreCase) -or $outputPath.Equals([IO.Path]::GetFullPath($StateFile), [StringComparison]::OrdinalIgnoreCase)) { throw 'OutputFile cannot overwrite either read-only input.' }
    $arguments += @('--cache', [IO.Path]::GetFullPath($SnapshotFile), '--state', [IO.Path]::GetFullPath($StateFile))
}
& (Join-Path $PSScriptRoot 'restore.ps1') -RuntimeIdentifier win-x64
& $sdk run --project $project --configuration Release --runtime win-x64 '-p:Platform=x64' --no-restore -- @arguments
if ($LASTEXITCODE -ne 0) { throw 'Performance regression failed.' }
