param(
    [Parameter(Mandatory = $true)][string]$ProjectAssetsPath,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [string]$DotnetPath
)

$ErrorActionPreference = 'Stop'
$assetFile = (Resolve-Path -LiteralPath $ProjectAssetsPath).Path
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$assets = [IO.File]::ReadAllText($assetFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$collectedRoot = Join-Path $publishRoot 'ThirdPartyLicenses'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packages = [ordered]@{}
$utf8 = New-Object Text.UTF8Encoding($false)

function Register-Package([string]$Id, [string]$Version, [string]$Kind) {
    if ($Version -notmatch '^\d+(\.\d+)+([-+][A-Za-z0-9.-]+)?$') {
        throw "An exact package version is required: $Id / $Version"
    }
    $key = "$Id/$Version"
    if (-not $packages.Contains($key)) {
        $packages[$key] = [ordered]@{ id = $Id; version = $Version; kind = $Kind }
    }
}

foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -eq 'package') {
        $identity = $library.Name.Split('/')
        Register-Package $identity[0] $identity[1] 'NuGet'
    }
}
foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
    foreach ($dependency in $framework.Value.downloadDependencies) {
        $version = $dependency.version.Trim('[', ']')
        $parts = $version.Split(',')
        if ($parts.Count -gt 1 -and $parts[0].Trim() -ne $parts[1].Trim()) {
            throw "The targeting/runtime pack is not pinned: $($dependency.name) $version"
        }
        Register-Package $dependency.name $parts[0].Trim() 'TargetingOrRuntimePack'
    }
}

$runtimeManifests = @(Get-ChildItem -LiteralPath $publishRoot -Filter '*.deps.json' -File)
foreach ($manifest in $runtimeManifests) {
    $runtimeDependencies = [IO.File]::ReadAllText($manifest.FullName, [Text.Encoding]::UTF8) | ConvertFrom-Json
    foreach ($library in $runtimeDependencies.libraries.PSObject.Properties) {
        $identity = $library.Name.Split('/')
        $id = $identity[0] -replace '^runtimepack\.', ''
        $resolved = @($packages.Values | Where-Object { $_.id -eq $id })
        if ($resolved.Count -gt 0 -and $resolved.version -notcontains $identity[1]) {
            throw "Publish output contains $($library.Name), but project assets resolve a different version. Rebuild the package before collecting notices."
        }
    }
}

function Find-PackageDirectory([string]$Id, [string]$Version) {
    foreach ($root in $packageRoots) {
        $candidate = Join-Path (Join-Path $root $Id.ToLowerInvariant()) $Version.ToLowerInvariant()
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    if ($DotnetPath) {
        $sdkRoot = Split-Path -Parent (Resolve-Path -LiteralPath $DotnetPath).Path
        $candidate = Join-Path (Join-Path (Join-Path $sdkRoot 'packs') $Id) $Version
        if (Test-Path -LiteralPath $candidate -PathType Container) { return $candidate }
    }
    throw "Resolved package content is missing: $Id/$Version. Restore the project first."
}

function Read-NodeText($Node, [string]$Name) {
    $child = $Node.SelectSingleNode("*[local-name()='$Name']")
    if ($null -eq $child) { return '' }
    return [string]$child.InnerText
}

function Copy-LegalFile([string]$Source, [string]$Target, [string]$Origin) {
    $sourceHash = (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash.ToLowerInvariant()
    if (Test-Path -LiteralPath $Target) {
        $targetHash = (Get-FileHash -LiteralPath $Target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($sourceHash -ne $targetHash) { throw "A different legal text already exists at $Target" }
    }
    else {
        [IO.Directory]::CreateDirectory((Split-Path -Parent $Target)) | Out-Null
        [IO.File]::Copy($Source, $Target, $false)
    }
    return [ordered]@{ path = $Target.Substring($publishRoot.Length + 1).Replace('\', '/'); sha256 = $sourceHash; origin = $Origin }
}

$inventory = New-Object 'System.Collections.Generic.List[object]'
$previewPackages = New-Object 'System.Collections.Generic.List[string]'
foreach ($entry in $packages.Values) {
    $packageDirectory = Find-PackageDirectory $entry.id $entry.version
    $nuspecFile = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
    if ($null -eq $nuspecFile) { throw "Package metadata is missing: $($entry.id)/$($entry.version)" }
    [xml]$nuspec = [IO.File]::ReadAllText($nuspecFile.FullName, [Text.Encoding]::UTF8)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ((Read-NodeText $metadata 'id') -ne $entry.id -or (Read-NodeText $metadata 'version') -ne $entry.version) {
        throw "Package identity does not match its resolved version: $($entry.id)/$($entry.version)"
    }
    $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
    $entry.licenseType = if ($null -eq $licenseNode) { 'url' } else { $licenseNode.GetAttribute('type') }
    $entry.licenseValue = if ($null -eq $licenseNode) { Read-NodeText $metadata 'licenseUrl' } else { [string]$licenseNode.InnerText }
    $entry.packageUrl = "https://www.nuget.org/packages/$($entry.id)/$($entry.version)"
    $entry.licenseUrl = if ($entry.licenseType -eq 'file') { $entry.packageUrl + '/License' } else { Read-NodeText $metadata 'licenseUrl' }
    $entry.projectUrl = Read-NodeText $metadata 'projectUrl'
    $entry.requiresLicenseAcceptance = (Read-NodeText $metadata 'requireLicenseAcceptance') -eq 'true'
    $legalFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Where-Object { $_.Name -match '(?i)license|notice' })
    if ($entry.licenseType -eq 'file') {
        $explicitPath = Join-Path $packageDirectory $entry.licenseValue
        if (-not (Test-Path -LiteralPath $explicitPath -PathType Leaf)) { throw "Declared license file is missing: $explicitPath" }
        if ($legalFiles.FullName -notcontains $explicitPath) { $legalFiles += Get-Item -LiteralPath $explicitPath }
    }
    $destination = Join-Path (Join-Path $collectedRoot $entry.id) $entry.version
    $files = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in $legalFiles) {
        if ($file.Extension -ne '.rtf') {
            $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
            if ($text -match 'MICROSOFT WINDOWS APP SDK ENGINEERING PREVIEW') {
                $previewPackages.Add("$($entry.id)/$($entry.version)")
            }
        }
        $files.Add((Copy-LegalFile $file.FullName (Join-Path $destination $file.Name) 'ResolvedNuGetPackage'))
    }
    if ($legalFiles.Count -eq 0 -and $entry.licenseValue -eq 'https://aka.ms/WinSDKLicenseURL') {
        $windowsSdkLicense = Join-Path $repositoryRoot 'docs/licenses/windows-sdk-license.rtf'
        if (-not (Test-Path -LiteralPath $windowsSdkLicense -PathType Leaf)) { throw 'The verified Windows SDK license copy is missing from docs/licenses.' }
        $files.Add((Copy-LegalFile $windowsSdkLicense (Join-Path $destination 'sdk_license.rtf') 'MicrosoftLicenseUrl'))
    }
    if ($files.Count -eq 0) { throw "No distributable license text found for $($entry.id)/$($entry.version)" }
    $entry.files = @($files.ToArray())
    $entry.engineeringPreviewLicense = $previewPackages.Contains("$($entry.id)/$($entry.version)")
    $inventory.Add($entry)
}

[IO.Directory]::CreateDirectory($collectedRoot) | Out-Null
$report = [ordered]@{
    schemaVersion = 1
    target = $assets.project.restore.projectName
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    packages = @($inventory.ToArray())
    releaseBlockers = @($previewPackages.ToArray())
    note = 'Original package legal files are copied byte-for-byte. No license is accepted by this script. Application MIT terms do not replace component terms. Existing SDK-emitted notices are preserved.'
}
[IO.File]::WriteAllText((Join-Path $collectedRoot 'inventory.json'), ($report | ConvertTo-Json -Depth 20) + [Environment]::NewLine, $utf8)
if ($previewPackages.Count -gt 0) {
    throw ('Release blocked by engineering-preview terms: ' + ($previewPackages -join ', '))
}
Write-Output "Collected legal texts for $($inventory.Count) exact package/runtime versions into $collectedRoot"
