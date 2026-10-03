param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$OutputDirectory = 'artifacts',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$OutputName = 'app-release',
    [ValidateSet('Both', 'Portable', 'SingleFile')][string]$PackageMode = 'Both',
    [string]$ReleaseDirectory = 'artifacts/releases',
    [switch]$ReplaceRelease
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Resolve-ProjectSdk
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$runtime = 'win-x64'
$outputParent = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repository $OutputDirectory }))
if ($OutputName -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or $OutputName.EndsWith('.')) { throw 'OutputName must be a safe Windows directory name.' }
$output = [IO.Path]::GetFullPath((Join-Path $outputParent $OutputName))
$protectedApp = [IO.Path]::GetFullPath((Join-Path $artifactsRoot 'app'))
function Assert-ArtifactPath([string]$Path, [switch]$AllowRoot) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (!($AllowRoot -and $absolute.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) -and !$absolute.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Build output escaped the repository artifacts directory.' }
    if ($absolute.Equals($protectedApp, [StringComparison]::OrdinalIgnoreCase) -or $absolute.StartsWith($protectedApp + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'artifacts/app is reserved for the currently running application. Choose another output.' }
    $ancestor = $absolute
    while ($ancestor.Length -ge $artifactsRoot.Length) {
        if (Test-Path -LiteralPath $ancestor) {
            if (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Build output cannot traverse a symbolic link or junction.' }
        }
        if ($ancestor.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $ancestor = Split-Path -Parent $ancestor
    }
}
Assert-ArtifactPath $outputParent -AllowRoot
Assert-ArtifactPath $output
$releaseRoot = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ReleaseDirectory)) { $ReleaseDirectory } else { Join-Path $repository $ReleaseDirectory }))
Assert-ArtifactPath $releaseRoot
$properties = [xml][IO.File]::ReadAllText((Join-Path $repository 'Directory.Build.props'))
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric project version.' }
$assetBase = Join-Path $releaseRoot ('SimpleScraper-' + $version + '-win-x64')
$releaseAssets = @()
if ($PackageMode -ne 'SingleFile') { $releaseAssets += $assetBase + '.zip' }
if ($PackageMode -ne 'Portable') { $releaseAssets += $assetBase + '.exe' }
foreach ($asset in $releaseAssets) {
    foreach ($path in @($asset, ($asset + '.sha256'))) {
        Assert-ArtifactPath $path
        if ((Test-Path -LiteralPath $path) -and !$ReplaceRelease) { throw ('A release asset already exists. Choose another ReleaseDirectory or explicitly use -ReplaceRelease: ' + $path) }
    }
}
foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name LIKE 'SimpleScraper%.exe'")) {
    if (!$process.ExecutablePath) { continue }
    if ($process.ExecutablePath.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'The selected output contains a running application. Close that instance or choose another OutputName.' }
    foreach ($asset in $releaseAssets) {
        if ($process.ExecutablePath.Equals($asset, [StringComparison]::OrdinalIgnoreCase)) { throw 'A selected release executable is running. Close that instance or choose another ReleaseDirectory.' }
    }
}
& (Join-Path $PSScriptRoot 'restore.ps1') -RuntimeIdentifier $runtime
if (!$SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') -SkipRestore -RuntimeIdentifier $runtime }
$app = Join-Path $repository 'src/SimpleScraper.App/SimpleScraper.App.csproj'
$assetsFile = Join-Path $repository 'src/SimpleScraper.App/obj/project.assets.json'
$stagingRoot = Join-Path $artifactsRoot ('build-' + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactPath $stagingRoot
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
$publishArguments = @('publish', $app, '-c', $Configuration, '-r', $runtime, '-p:Platform=x64', '--self-contained', 'true', '--no-restore', '-p:PublishReadyToRun=false', '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-v', 'minimal')
if ($PackageMode -ne 'SingleFile') {
    $portable = Join-Path $stagingRoot 'portable'
    & $sdk @publishArguments '-p:PublishSingleFile=false' -o $portable
    if ($LASTEXITCODE -ne 0) { throw 'Portable app publish failed.' }
    foreach ($resource in @('App.xbf', 'Views/MainWindow.xbf', 'Views/LibraryPage.xbf', 'Styles/FluentStyles.xbf', 'Assets/SimpleScraper.ico', 'Assets/SimpleScraper.png', 'resources.pri')) {
        if (!(Test-Path -LiteralPath (Join-Path $portable $resource))) { throw ('Published WinUI resource missing: ' + $resource) }
    }
    if (Test-Path -LiteralPath (Join-Path $portable 'SimpleScraper.pri')) { throw 'Portable publish retained an obsolete app PRI filename. Use a fresh publish directory.' }
    $portableManifest = & (Join-Path $PSScriptRoot 'verify-winrt-manifest.ps1') -Executable (Join-Path $portable 'SimpleScraper.exe') -ProjectAssetsPath $assetsFile -Mode Portable -ReportPath (Join-Path $stagingRoot 'portable-manifest-verification.json')
    Write-Output ('Verified portable WinRT manifest: ' + $portableManifest.nativeFileCount + ' native files with app-local activation.')
    & (Join-Path $PSScriptRoot 'collect-runtime-notices.ps1') -ProjectAssetsPath $assetsFile -PublishDirectory $portable -DotnetPath $sdk
    [IO.File]::Copy((Join-Path $repository 'docs/USER_GUIDE.md'), (Join-Path $portable 'USER_GUIDE.md'), $true)
}
if ($PackageMode -ne 'Portable') {
    $single = Join-Path $stagingRoot 'single-file'
    & $sdk @publishArguments '-p:PublishSingleFile=true' -o $single
    if ($LASTEXITCODE -ne 0) { throw 'Single-file app publish failed.' }
    $xbfDirectory = Join-Path $repository ('src/SimpleScraper.App/bin/x64/' + $Configuration + '/net10.0-windows10.0.26100.0/' + $runtime)
    & (Join-Path $PSScriptRoot 'verify-single-file.ps1') -Executable (Join-Path $single 'SimpleScraper.exe') -ProjectAssetsPath $assetsFile -DotnetPath $sdk -TargetXbfDirectory $xbfDirectory -ReportPath (Join-Path $stagingRoot 'single-file-verification.json')
    $singleContents = @(Get-ChildItem -LiteralPath $single -Force)
    if ($singleContents.Count -ne 1 -or $singleContents[0].Name -ne 'SimpleScraper.exe') { throw 'Single-file publish must contain exactly one executable.' }
}
# Publish only after both profiles have passed validation; failed staging remains inspectable.
$selectedOutput = if ($PackageMode -eq 'SingleFile') { $single } else { $portable }
$backupOutput = Join-Path $artifactsRoot ('previous-' + $OutputName + '-' + [Guid]::NewGuid().ToString('N'))
foreach ($candidate in @($selectedOutput, $output, $backupOutput)) { Assert-ArtifactPath $candidate }
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
if (Test-Path -LiteralPath $output) { Move-Item -LiteralPath $output -Destination $backupOutput }
Move-Item -LiteralPath $selectedOutput -Destination $output
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
if ($PackageMode -ne 'SingleFile') { Compress-Archive -Path (Join-Path $output '*') -DestinationPath ($assetBase + '.zip') -Force }
if ($PackageMode -ne 'Portable') {
    $singleSource = if ($PackageMode -eq 'SingleFile') { Join-Path $output 'SimpleScraper.exe' } else { Join-Path $single 'SimpleScraper.exe' }
    [IO.File]::Copy($singleSource, ($assetBase + '.exe'), $ReplaceRelease.IsPresent)
    [IO.File]::Copy((Join-Path $stagingRoot 'single-file-verification.json'), (Join-Path $outputParent ($OutputName + '-single-file-verification.json')), $true)
}
foreach ($asset in $releaseAssets) {
    $checksum = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($asset + '.sha256'), ($checksum + '  ' + [IO.Path]::GetFileName($asset) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Output ('Release: ' + $asset)
}
Write-Output ('App: ' + (Join-Path $output 'SimpleScraper.exe'))
