param(
    [string]$PublishDirectory = 'artifacts/app-release',
    [string]$ReleaseDirectory = 'artifacts/releases',
    [string]$CompilerPath,
    [ValidatePattern('^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$RepositoryUrl = 'https://github.com/v-hiker/simple-scraper',
    [switch]$ReplaceRelease
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repository 'artifacts'
function Resolve-RepositoryPath([string]$Path) {
    [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repository $Path }))
}
function Assert-ArtifactPath([string]$Path) {
    if (!$Path.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer output must stay inside the repository artifacts directory.' }
    $ancestor = $Path
    while ($ancestor.Length -ge $artifactsRoot.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Installer output cannot traverse a symbolic link or junction.' }
        if ($ancestor.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $ancestor = Split-Path -Parent $ancestor
    }
}
$publishRoot = Resolve-RepositoryPath $PublishDirectory
$releaseRoot = Resolve-RepositoryPath $ReleaseDirectory
Assert-ArtifactPath $releaseRoot
foreach ($file in @('SimpleScraper.exe', 'resources.pri', 'Assets/SimpleScraper.ico', 'App.xbf', 'Views/MainWindow.xbf', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'USER_GUIDE.md')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot $file) -PathType Leaf)) { throw ('Portable publish file missing: ' + $file) }
}
if (!(Test-Path -LiteralPath (Join-Path $publishRoot 'ThirdPartyLicenses') -PathType Container)) { throw 'Portable publish runtime licenses are missing.' }
foreach ($runtimeFile in @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.dll', 'Microsoft.WinUI.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $publishRoot $runtimeFile) -PathType Leaf)) { throw ('Self-contained runtime file missing: ' + $runtimeFile) }
}
if (!$CompilerPath) {
    $candidates = @((Join-Path $artifactsRoot 'tools/inno-setup/ISCC.exe'), (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'), (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'))
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath -PathType Leaf)) { throw 'Inno Setup compiler not found. Run scripts/setup-inno.ps1 or pass -CompilerPath.' }
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$properties = [xml][IO.File]::ReadAllText((Join-Path $repository 'Directory.Build.props'))
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric project version.' }
$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishRoot 'SimpleScraper.exe')).VersionInfo.ProductVersion.Split('+')[0]
if ($publishedVersion -ne $version) { throw ('Portable publish version ' + $publishedVersion + ' does not match project version ' + $version + '. Build a fresh portable package first.') }
$asset = Join-Path $releaseRoot ('SimpleScraper-' + $version + '-win-x64-setup.exe')
foreach ($path in @($asset, ($asset + '.sha256'))) {
    Assert-ArtifactPath $path
    if ((Test-Path -LiteralPath $path) -and !$ReplaceRelease) { throw ('Installer asset already exists. Use -ReplaceRelease or another ReleaseDirectory: ' + $path) }
}
$stage = Join-Path $artifactsRoot ('installer-' + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactPath $stage
[IO.Directory]::CreateDirectory($stage) | Out-Null
& $CompilerPath '/Q' ('/DAppVersion=' + $version) ('/DPublishDirectory=' + $publishRoot) ('/DReleaseDirectory=' + $stage) ('/DRepositoryUrl=' + $RepositoryUrl) (Join-Path $repository 'installer/SimpleScraper.iss')
if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
$compiled = Join-Path $stage ([IO.Path]::GetFileName($asset))
if (!(Test-Path -LiteralPath $compiled -PathType Leaf)) { throw 'Compiler did not produce the expected installer.' }
$checksum = (Get-FileHash -LiteralPath $compiled -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
[IO.File]::Copy($compiled, $asset, $ReplaceRelease.IsPresent)
[IO.File]::WriteAllText(($asset + '.sha256'), ($checksum + '  ' + [IO.Path]::GetFileName($asset) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Output ('Installer: ' + $asset)
