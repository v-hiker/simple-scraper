param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$ReportPath,
    [string]$ProjectAssetsPath,
    [string]$DotnetPath,
    [string]$TargetXbfDirectory,
    [switch]$AllowExternalFiles
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ProjectAssetsPath) { $ProjectAssetsPath = Join-Path $repository 'src/SimpleScraper.App/obj/project.assets.json' }
if (!$DotnetPath) { . (Join-Path $PSScriptRoot 'sdk.ps1'); $DotnetPath = Resolve-ProjectSdk }
if (!$TargetXbfDirectory) { $TargetXbfDirectory = Join-Path $repository 'src/SimpleScraper.App/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64' }
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$assetPath = (Resolve-Path -LiteralPath $ProjectAssetsPath).Path
$sdkRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotnetPath).Path
if (!$ReportPath) { $ReportPath = Join-Path $repository 'artifacts/verification/single-file-release.json' }
$reportAbsolute = [IO.Path]::GetFullPath($ReportPath)
if ($reportAbsolute.Equals($executablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'ReportPath must not overwrite the executable.' }
$utf8 = [Text.UTF8Encoding]::new($false)
$report = [ordered]@{ schemaVersion = 1; status = 'failed'; executable = $executablePath; verifiedUtc = [DateTime]::UtcNow.ToString('o') }
$temporaryDirectory = $null
$temporaryFiles = New-Object 'System.Collections.Generic.List[string]'

try {
    if (!('SimpleScraper.Packaging.BundleInspector' -as [type])) {
        Add-Type -Path (Join-Path $PSScriptRoot 'SingleFileBundleInspector.cs')
    }
    $bundle = [SimpleScraper.Packaging.BundleInspector]::Inspect($executablePath)
    if (!$bundle.ExtractAllContent) { throw 'WinUI single-file deployment requires IncludeAllContentForSelfExtract=true.' }
    $entryByPath = New-Object 'System.Collections.Generic.Dictionary[string,SimpleScraper.Packaging.BundleEntry]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $bundle.Entries) { $entryByPath.Add($entry.Path, $entry) }
    if ($entryByPath.ContainsKey('SimpleScraper.pri')) { throw 'The bundle retained a stale executable-name-dependent SimpleScraper.pri resource file.' }
    if (!$entryByPath.ContainsKey('resources.pri')) { throw 'The bundle requires the common resources.pri name for renamed-executable startup.' }
    function Get-BundleEntry([string]$Path) {
        if (!$entryByPath.ContainsKey($Path)) { throw ('Required bundle entry is missing: ' + $Path) }
        return $entryByPath[$Path]
    }
    function Get-BundleText([string]$Path) {
        return [Text.Encoding]::UTF8.GetString([SimpleScraper.Packaging.BundleInspector]::ReadEntryContent($executablePath, (Get-BundleEntry $Path)))
    }
    function Assert-BundleMatchesFile([string]$BundledPath, [string]$OriginalPath) {
        if (!(Test-Path -LiteralPath $OriginalPath -PathType Leaf)) { throw ('Reference file is missing: ' + $OriginalPath) }
        $entry = Get-BundleEntry $BundledPath
        $sourceHash = [SimpleScraper.Packaging.BundleInspector]::HashFile($OriginalPath)
        if ($entry.Sha256 -cne $sourceHash) { throw ('Bundled bytes differ from the current source: ' + $BundledPath) }
        return [ordered]@{ path = $BundledPath; sha256 = $sourceHash; bytes = $entry.OriginalBytes }
    }

    $report.bundleVersion = $bundle.Version
    $report.bundleId = $bundle.BundleId
    $report.executableSha256 = $bundle.ExecutableSha256
    $report.executableBytes = $bundle.ExecutableBytes
    $report.entryCount = $bundle.Entries.Count
    $report.originalPayloadBytes = $bundle.OriginalPayloadBytes
    $report.storedPayloadBytes = $bundle.StoredPayloadBytes
    $report.manifestBytes = $bundle.ManifestBytes
    $report.allEntryDecompressionAndBundleIdVerified = $true
    $report.entries = @($bundle.Entries | ForEach-Object { [ordered]@{ path = $_.Path; type = $_.TypeName; originalBytes = $_.OriginalBytes; compressedBytes = $_.CompressedBytes; storedBytes = $_.StoredBytes; sha256 = $_.Sha256 } })

    $nativeNames = @($bundle.Entries | Where-Object { $_.Type -eq 2 } | ForEach-Object { $_.Path })
    $report.winrtManifest = & (Join-Path $PSScriptRoot 'verify-winrt-manifest.ps1') -Executable $executablePath -Mode SingleFile -ProjectAssetsPath $assetPath -DotnetPath $DotnetPath -NativePayloadPaths $nativeNames

    foreach ($entry in $bundle.Entries) {
        $name = [IO.Path]::GetFileName($entry.Path)
        if ($entry.Path -match '(^|/)(\.git|\.ssh|state|cache|logs|sessions|userdata|user-data)(/|$)' -or
            $name -match '(?i)^(appsettings|settings|config|session|state|credentials|secrets)(\.[^.]+)?\.(json|xml|ini|toml|yaml|yml)$|\.(pfx|p12|pem|key|keystore|sqlite|sqlite3|db|log)$|^id_(rsa|ed25519|ecdsa)$|\.runtimeconfig\.dev\.json$') {
            throw ('Private configuration, keys, cache or state appeared in the bundle: ' + $entry.Path)
        }
        if ($name -match '\.runtimeconfig\.json$' -and $entry.Type -ne 4) { throw ('Unexpected runtime configuration: ' + $entry.Path) }
        if ($name -match '\.deps\.json$' -and $entry.Type -ne 3) { throw ('Unexpected dependency configuration: ' + $entry.Path) }
    }
    $runtimeEntry = @($bundle.Entries | Where-Object { $_.Type -eq 4 })[0]
    $depsEntry = @($bundle.Entries | Where-Object { $_.Type -eq 3 })[0]
    $runtime = Get-BundleText $runtimeEntry.Path | ConvertFrom-Json
    if ($runtime.runtimeOptions.tfm -ne 'net10.0' -or @($runtime.runtimeOptions.includedFrameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).Count -ne 1) { throw 'The bundle is not a self-contained .NET 10 app.' }
    if ($runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks -or $runtime.runtimeOptions.additionalProbingPaths) { throw 'The runtime configuration contains an external runtime or probing-path dependency.' }
    $runtimeDependencies = Get-BundleText $depsEntry.Path | ConvertFrom-Json

    $references = New-Object 'System.Collections.Generic.List[object]'
    foreach ($pair in @(
        @('LICENSE', (Join-Path $repository 'LICENSE')),
        @('THIRD-PARTY-NOTICES.md', (Join-Path $repository 'THIRD-PARTY-NOTICES.md')),
        @('USER_GUIDE.md', (Join-Path $repository 'docs/USER_GUIDE.md')),
        @('Assets/SimpleScraper.ico', (Join-Path $repository 'src/SimpleScraper.App/Assets/SimpleScraper.ico')),
        @('Assets/SimpleScraper.png', (Join-Path $repository 'src/SimpleScraper.App/Assets/SimpleScraper.png'))
    )) { $references.Add((Assert-BundleMatchesFile $pair[0] $pair[1])) }
    $report.currentSourceAssetsVerified = @($references.ToArray())

    $assets = [IO.File]::ReadAllText($assetPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $resolvedPackages = [ordered]@{}
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq 'package') { $parts = $library.Name.Split('/'); $resolvedPackages[$library.Name] = [ordered]@{ id = $parts[0]; version = $parts[1] } }
    }
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($dependency in $framework.Value.downloadDependencies) {
            $parts = $dependency.version.Trim('[', ']').Split(',')
            if ($parts.Count -gt 1 -and $parts[0].Trim() -ne $parts[1].Trim()) { throw ('Unpinned SDK pack: ' + $dependency.name) }
            $version = $parts[0].Trim()
            $resolvedPackages[$dependency.name + '/' + $version] = [ordered]@{ id = $dependency.name; version = $version }
        }
    }
    function Find-ResolvedPackage([string]$Id, [string]$Version) {
        foreach ($root in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path (Join-Path $root $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
            if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
        }
        $candidate = Join-Path (Join-Path (Join-Path $sdkRoot 'packs') $Id) $Version
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
        throw ('Resolved package content is unavailable: ' + $Id + '/' + $Version)
    }
    $inventory = Get-BundleText 'ThirdPartyLicenses/inventory.json' | ConvertFrom-Json
    if ($inventory.schemaVersion -ne 1 -or @($inventory.releaseBlockers).Count -ne 0) { throw 'The embedded license inventory is invalid or records a release blocker.' }
    if (@($inventory.packages).Count -ne $resolvedPackages.Count) { throw 'The license inventory does not cover the current resolved package graph.' }
    $packageKeys = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $legalPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $verifiedLegal = New-Object 'System.Collections.Generic.List[object]'
    foreach ($package in $inventory.packages) {
        $key = $package.id + '/' + $package.version
        if (!$resolvedPackages.Contains($key) -or !$packageKeys.Add($key) -or $package.engineeringPreviewLicense) { throw ('The embedded inventory has an unexpected, duplicate or blocked package: ' + $key) }
        $packageDirectory = Find-ResolvedPackage $package.id $package.version
        $originalLegal = [ordered]@{}
        foreach ($source in [IO.Directory]::GetFiles($packageDirectory)) {
            if ([IO.Path]::GetFileName($source) -match '(?i)license|notice') { $originalLegal[[IO.Path]::GetFileName($source)] = $source }
        }
        $nuspecPath = @([IO.Directory]::GetFiles($packageDirectory, '*.nuspec'))
        if ($nuspecPath.Count -ne 1) { throw ('Package metadata is ambiguous: ' + $key) }
        [xml]$nuspec = [IO.File]::ReadAllText($nuspecPath[0], [Text.Encoding]::UTF8)
        $metadata = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $license = $metadata.SelectSingleNode('*[local-name()="license"]')
        if ($license -and $license.GetAttribute('type') -eq 'file') {
            $declared = [IO.Path]::GetFullPath((Join-Path $packageDirectory $license.InnerText))
            if (!$declared.StartsWith([IO.Path]::GetFullPath($packageDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw ('Declared license escaped its package: ' + $key) }
            $originalLegal[[IO.Path]::GetFileName($declared)] = $declared
        }
        $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        if ($originalLegal.Count -eq 0 -and $licenseUrl -and $licenseUrl.InnerText -eq 'https://aka.ms/WinSDKLicenseURL') { $originalLegal['sdk_license.rtf'] = Join-Path $repository 'docs/licenses/windows-sdk-license.rtf' }
        if ($originalLegal.Count -eq 0 -or @($package.files).Count -ne $originalLegal.Count) { throw ('Missing or unexpected original legal files for ' + $key) }
        foreach ($file in $package.files) {
            $prefix = 'ThirdPartyLicenses/' + $key + '/'
            if (!$file.path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or !$legalPaths.Add($file.path)) { throw ('Unexpected or duplicate legal-file path: ' + $file.path) }
            $name = $file.path.Substring($prefix.Length)
            if (!$originalLegal.Contains($name)) { throw ('The legal file has no original package source: ' + $file.path) }
            $verified = Assert-BundleMatchesFile $file.path $originalLegal[$name]
            if ($verified.sha256 -cne $file.sha256) { throw ('Inventory hash does not match the original legal file: ' + $file.path) }
            if ($name -notmatch '\.rtf$' -and (Get-BundleText $file.path) -match 'MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW') { throw ('Engineering-preview license appeared in the bundle: ' + $file.path) }
            $verifiedLegal.Add($verified)
        }
    }
    foreach ($library in $runtimeDependencies.libraries.PSObject.Properties) {
        $parts = $library.Name.Split('/')
        $id = $parts[0] -replace '^runtimepack\.', ''
        $found = @($resolvedPackages.Values | Where-Object { $_.id -eq $id })
        if ($found.Count -gt 0 -and $found.version -notcontains $parts[1]) { throw ('The embedded runtime graph has a stale dependency: ' + $library.Name) }
    }
    $report.packageVersionCount = $packageKeys.Count
    $report.originalLegalFileCount = $legalPaths.Count
    $report.legalFiles = @($verifiedLegal.ToArray())

    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryParent ('SimpleScraper-pri-' + [Guid]::NewGuid().ToString('N'))))
    if (!$temporaryDirectory.StartsWith($temporaryParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid verification temporary directory.' }
    [IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
    $priPath = Join-Path $temporaryDirectory 'resources.pri'
    $dumpPath = Join-Path $temporaryDirectory 'SimpleScraper-pri.xml'
    $temporaryFiles.Add($priPath); $temporaryFiles.Add($dumpPath)
    [IO.File]::WriteAllBytes($priPath, [SimpleScraper.Packaging.BundleInspector]::ReadEntryContent($executablePath, (Get-BundleEntry 'resources.pri')))
    $makePri = Join-Path (Split-Path -Parent $report.winrtManifest.sdkManifestTool) 'makepri.exe'
    if (!(Test-Path -LiteralPath $makePri -PathType Leaf)) { throw 'The pinned SDK makepri.exe tool is unavailable.' }
    $toolOutput = & $makePri dump /if $priPath /of $dumpPath /dt detailed 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $dumpPath -PathType Leaf)) { throw ('Embedded PRI decoding failed: ' + $toolOutput.Trim()) }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($dumpPath, $settings)
    $pri = New-Object Xml.XmlDocument; $pri.XmlResolver = $null
    try { $pri.Load($reader) } finally { $reader.Dispose() }
    $xbfChecks = New-Object 'System.Collections.Generic.List[object]'
    foreach ($path in @('App.xbf', 'Styles/FluentStyles.xbf', 'Views/LibraryPage.xbf', 'Views/MainWindow.xbf')) {
        $uri = 'ms-resource://SimpleScraper/Files/' + $path
        $nodes = @($pri.SelectNodes('//*[local-name()="NamedResource"]') | Where-Object { $_.GetAttribute('uri') -ceq $uri })
        if ($nodes.Count -ne 1) { throw ('Expected XBF resource is missing or duplicated in the PRI: ' + $path) }
        $candidates = @($nodes[0].SelectNodes('./*[local-name()="Candidate"]'))
        if ($candidates.Count -ne 1 -or $candidates[0].GetAttribute('type') -ne 'EmbeddedData') { throw ('XBF is not embedded as binary PRI data: ' + $path) }
        $base64 = $candidates[0].SelectSingleNode('./*[local-name()="Base64Value"]')
        if (!$base64) { throw ('Embedded XBF bytes are missing: ' + $path) }
        $bytes = [Convert]::FromBase64String($base64.InnerText)
        if ($bytes.Length -lt 4 -or [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -cne "XBF`0") { throw ('Invalid XBF signature: ' + $path) }
        $hash = [SimpleScraper.Packaging.BundleInspector]::HashBytes($bytes)
        $original = Join-Path $TargetXbfDirectory $path
        if (!(Test-Path -LiteralPath $original -PathType Leaf) -or $hash -cne [SimpleScraper.Packaging.BundleInspector]::HashFile($original)) { throw ('Embedded XBF differs from the current compiler output: ' + $path) }
        if (!$entryByPath.ContainsKey($path)) { throw ('Explicit XBF bundle entry is required for the verified WinUI startup path: ' + $path) }
        if ($entryByPath[$path].Sha256 -cne $hash) { throw ('Explicit XBF bundle entry differs from its PRI copy: ' + $path) }
        $xbfChecks.Add([ordered]@{ path = $path; bytes = $bytes.Length; sha256 = $hash; embeddedPriDataVerified = $true; explicitBundleEntry = $true })
    }
    $report.xbfResources = @($xbfChecks.ToArray())

    $externalFiles = @([IO.Directory]::GetFiles((Split-Path -Parent $executablePath), '*', [IO.SearchOption]::AllDirectories) | Where-Object { !$_.Equals($executablePath, [StringComparison]::OrdinalIgnoreCase) -and !$_.Equals($reportAbsolute, [StringComparison]::OrdinalIgnoreCase) })
    $report.externalFiles = $externalFiles
    if (!$AllowExternalFiles -and $externalFiles.Count -ne 0) { throw 'Single-file output contains external files. They were preserved; publish into a fresh directory and bundle the required contents.' }
    if ([SimpleScraper.Packaging.BundleInspector]::HashFile($executablePath) -cne $bundle.ExecutableSha256) { throw 'The executable changed during verification.' }
    $report.privateConfigStateAndKeysAbsent = $true
    $report.status = 'passed'
}
catch { $report.error = $_.Exception.Message; throw }
finally {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $reportAbsolute)) | Out-Null
    [IO.File]::WriteAllText($reportAbsolute, ($report | ConvertTo-Json -Depth 25) + [Environment]::NewLine, $utf8)
    foreach ($file in $temporaryFiles) { if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file -Force } }
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory -PathType Container)) { [IO.Directory]::Delete($temporaryDirectory, $false) }
}
Write-Output ('Verified single-file EXE: ' + $bundle.Entries.Count + ' entries, ' + $report.packageVersionCount + ' dependency versions, ' + $report.originalLegalFileCount + ' original legal files. Report: ' + $reportAbsolute)
