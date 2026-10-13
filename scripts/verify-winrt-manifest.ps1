param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [ValidateSet('SingleFile', 'Portable')][string]$Mode = 'Portable',
    [string]$ProjectAssetsPath,
    [string]$DotnetPath,
    [string]$ReportPath,
    [string[]]$NativePayloadPaths
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$ProjectAssetsPath) { $ProjectAssetsPath = Join-Path $repository 'src/SimpleScraper.App/obj/project.assets.json' }
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$assetPath = (Resolve-Path -LiteralPath $ProjectAssetsPath).Path
$assets = [IO.File]::ReadAllText($assetPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
$buildTools = @($assets.libraries.PSObject.Properties | Where-Object { $_.Name -match '^Microsoft\.Windows\.SDK\.BuildTools/' })
if ($buildTools.Count -ne 1) { throw 'The assets must resolve exactly one Microsoft.Windows.SDK.BuildTools version.' }
$identity = $buildTools[0].Name.Split('/')
$manifestTool = $null
foreach ($packageRoot in $assets.packageFolders.PSObject.Properties.Name) {
    $packageDirectory = Join-Path (Join-Path $packageRoot $identity[0].ToLowerInvariant()) $identity[1].ToLowerInvariant()
    $binDirectory = Join-Path $packageDirectory 'bin'
    if (Test-Path -LiteralPath $binDirectory -PathType Container) {
        foreach ($versionDirectory in [IO.Directory]::GetDirectories($binDirectory)) {
            $candidate = Join-Path $versionDirectory 'x64/mt.exe'
            if (Test-Path -LiteralPath $candidate -PathType Leaf) { $manifestTool = $candidate; break }
        }
    }
    if ($manifestTool) { break }
}
if (!$manifestTool) { throw 'The pinned Windows SDK manifest tool is missing. Restore the project first.' }

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$temporaryDirectory = [IO.Path]::GetFullPath((Join-Path $temporaryParent ('SimpleScraper-manifest-' + [Guid]::NewGuid().ToString('N'))))
if (!$temporaryDirectory.StartsWith($temporaryParent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid verification temporary directory.' }
[IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
$manifestPath = Join-Path $temporaryDirectory 'embedded.manifest'
$result = [ordered]@{ schemaVersion = 1; status = 'failed'; mode = $Mode; executable = $executablePath; sdkManifestTool = $manifestTool }
try {
    $toolOutput = & $manifestTool ('-inputresource:' + $executablePath + ';#1') ('-out:' + $manifestPath) 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw ('Embedded PE manifest extraction failed: ' + $toolOutput.Trim()) }
    $settings = New-Object Xml.XmlReaderSettings
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($manifestPath, $settings)
    $document = New-Object Xml.XmlDocument
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    $files = @($document.SelectNodes('//*[local-name()="file"]'))
    if ($files.Count -eq 0) { throw 'The embedded manifest contains no app-local WinRT activation files.' }
    $registrations = New-Object 'System.Collections.Generic.List[object]'
    $hasApplication = $false
    foreach ($file in $files) {
        $name = $file.GetAttribute('name')
        if (!$name -or $name -ne [IO.Path]::GetFileName($name) -or $name.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0) { throw ('Unsafe manifest native filename: ' + $name) }
        $loadFrom = $file.GetAttribute('loadFrom')
        if ($Mode -eq 'SingleFile') {
            $expected = '%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%' + $name
            if ($loadFrom -cne $expected) { throw ('Single-file EXE has a stale or incorrect SxS redirection for ' + $name + ': ' + $loadFrom) }
            if ($NativePayloadPaths -and $NativePayloadPaths -notcontains $name) { throw ('Manifest native DLL is missing from the bundle: ' + $name) }
        }
        else {
            if ($file.HasAttribute('loadFrom')) { throw ('Portable EXE retained single-file SxS redirection for ' + $name) }
            if (!(Test-Path -LiteralPath (Join-Path (Split-Path -Parent $executablePath) $name) -PathType Leaf)) { throw ('Portable native DLL is missing: ' + $name) }
        }
        $classes = @($file.SelectNodes('./*[local-name()="activatableClass"]'))
        foreach ($class in $classes) {
            if ($class.GetAttribute('name') -eq 'Microsoft.UI.Xaml.Application') {
                if ($name -ine 'Microsoft.UI.Xaml.dll') { throw 'Application activation is mapped to the wrong native DLL.' }
                $hasApplication = $true
            }
        }
        $registrations.Add([ordered]@{ file = $name; loadFrom = $loadFrom; activatableClassCount = $classes.Count })
    }
    if (!$hasApplication) { throw 'The embedded manifest does not register Microsoft.UI.Xaml.Application.' }
    $result.embeddedManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.nativeFileCount = $files.Count
    $result.applicationActivationVerified = $hasApplication
    $result.registrations = @($registrations.ToArray())
    $result.status = 'passed'
}
catch { $result.error = $_.Exception.Message; throw }
finally {
    if ($ReportPath) {
        $reportAbsolute = [IO.Path]::GetFullPath($ReportPath)
        if ($reportAbsolute.Equals($executablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'ReportPath must not overwrite the executable.' }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $reportAbsolute)) | Out-Null
        [IO.File]::WriteAllText($reportAbsolute, ($result | ConvertTo-Json -Depth 15) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    }
    if (Test-Path -LiteralPath $manifestPath -PathType Leaf) { Remove-Item -LiteralPath $manifestPath -Force }
    if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) { [IO.Directory]::Delete($temporaryDirectory, $false) }
}
[pscustomobject]$result
