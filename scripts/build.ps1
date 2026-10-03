param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$OutputDirectory = 'artifacts',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')][string]$OutputName = 'app-release'
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
& (Join-Path $PSScriptRoot 'restore.ps1') -RuntimeIdentifier $runtime
if (!$SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') -SkipRestore -RuntimeIdentifier $runtime }
$app = Join-Path $repository 'src/SimpleScraper.App/SimpleScraper.App.csproj'
$staging = Join-Path $artifactsRoot ('build-' + [Guid]::NewGuid().ToString('N'))
Assert-ArtifactPath $staging
New-Item -ItemType Directory -Path $staging | Out-Null
& $sdk publish $app -c $Configuration -r $runtime '-p:Platform=x64' --self-contained true --no-restore -o $staging -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Windows app publish failed.' }
foreach ($resource in @('App.xbf', 'Views/MainWindow.xbf', 'Views/LibraryPage.xbf', 'Styles/FluentStyles.xbf', 'Assets/SimpleScraper.ico', 'Assets/SimpleScraper.png')) {
    if (!(Test-Path -LiteralPath (Join-Path $staging $resource))) { throw ('Published WinUI resource missing: ' + $resource) }
}
& (Join-Path $PSScriptRoot 'collect-runtime-notices.ps1') -ProjectAssetsPath (Join-Path $repository 'src/SimpleScraper.App/obj/project.assets.json') -PublishDirectory $staging -DotnetPath $sdk
$backupOutput = Join-Path $artifactsRoot ('previous-' + $OutputName + '-' + [Guid]::NewGuid().ToString('N'))
foreach ($candidate in @($staging, $output, $backupOutput)) {
    Assert-ArtifactPath $candidate
}
[IO.Directory]::CreateDirectory($outputParent) | Out-Null
if (Test-Path -LiteralPath $output) { Move-Item -LiteralPath $output -Destination $backupOutput }
Move-Item -LiteralPath $staging -Destination $output
Copy-Item -LiteralPath (Join-Path $repository 'docs/USER_GUIDE.md') -Destination (Join-Path $output 'USER_GUIDE.md') -Force
$properties = [xml][IO.File]::ReadAllText((Join-Path $repository 'Directory.Build.props'))
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a three-part numeric project version.' }
$releaseDirectory = Join-Path $repository 'artifacts/releases'
Assert-ArtifactPath $releaseDirectory
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$archive = Join-Path $releaseDirectory ('SimpleScraper-' + $version + '-win-x64.zip')
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($archive + '.sha256'), ($checksum + '  ' + [IO.Path]::GetFileName($archive) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Output ('App: ' + (Join-Path $output 'SimpleScraper.exe'))
Write-Output ('Release: ' + $archive)
