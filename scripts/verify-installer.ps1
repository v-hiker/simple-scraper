param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [string]$ReportDirectory = 'artifacts/verification/installer'
)
$ErrorActionPreference = 'Stop'
function Test-SameWindowsPath([string]$Actual, [string]$Expected) {
    if ([string]::IsNullOrWhiteSpace($Actual) -or [string]::IsNullOrWhiteSpace($Expected)) { return $false }
    $actualPath = [IO.Path]::GetFullPath($Actual).TrimEnd([char[]]@('\', '/'))
    $expectedPath = [IO.Path]::GetFullPath($Expected).TrimEnd([char[]]@('\', '/'))
    return $actualPath.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repository 'artifacts'
$installerPath = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Installer)) { $Installer } else { Join-Path $repository $Installer }))
if (!(Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw 'Installer executable not found.' }
$reportRoot = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ReportDirectory)) { $ReportDirectory } else { Join-Path $repository $ReportDirectory }))
if (!$reportRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Installer verification output must stay inside the repository artifacts directory.' }
$ancestor = $reportRoot
while ($ancestor.Length -ge $artifactsRoot.Length) {
    if ((Test-Path -LiteralPath $ancestor) -and (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Installer verification cannot traverse a symbolic link or junction.' }
    if ($ancestor.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = Split-Path -Parent $ancestor
}
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\SimpleScraper_is1'
function Read-Installation {
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            $key = $baseKey.OpenSubKey($uninstallKey)
            if ($key) {
                try {
                    return [pscustomobject]@{
                        DisplayName = [string]$key.GetValue('DisplayName')
                        DisplayVersion = [string]$key.GetValue('DisplayVersion')
                        InstallLocation = [string]$key.GetValue('InstallLocation')
                        UninstallString = [string]$key.GetValue('UninstallString')
                    }
                } finally { $key.Dispose() }
            }
        } finally { $baseKey.Dispose() }
    }
    return $null
}
if (Read-Installation) { throw 'Installer verification requires a clean user profile. An installed SimpleScraper was found.' }
$appPathKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\SimpleScraper.exe'
if (Test-Path -LiteralPath $appPathKey) { throw 'Installer verification requires a clean user profile. A SimpleScraper App Paths registration already exists.' }
$appName = -join @([char]0x7b80, [char]0x5355, [char]0x522e, [char]0x524a, [char]0x5668)
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) ($appName + '.lnk')
if (Test-Path -LiteralPath $shortcutPath) { throw 'Installer verification requires a clean Start Menu. A SimpleScraper shortcut already exists.' }
$properties = [xml][IO.File]::ReadAllText((Join-Path $repository 'Directory.Build.props'))
$version = [string]$properties.Project.PropertyGroup.Version
$runRoot = Join-Path $reportRoot ([Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $runRoot 'app'
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$windowsPowerShell = Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
$shortcutReader = Join-Path $PSScriptRoot 'read-shortcut.ps1'
if (!(Test-Path -LiteralPath $windowsPowerShell -PathType Leaf) -or !(Test-Path -LiteralPath $shortcutReader -PathType Leaf)) { throw 'Windows PowerShell STA shortcut reader is missing.' }
$hostReport = [ordered]@{powerShellVersion = $PSVersionTable.PSVersion.ToString(); powerShellEdition = [string]$PSVersionTable.PSEdition; apartmentState = [Threading.Thread]::CurrentThread.GetApartmentState().ToString()}
[IO.File]::WriteAllText((Join-Path $runRoot 'verification-host.json'), (($hostReport | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
$dataRoot = Join-Path $env:LOCALAPPDATA 'SimpleScraper'
[IO.Directory]::CreateDirectory($dataRoot) | Out-Null
$sentinelPath = Join-Path $dataRoot ('installer-verification-' + [Guid]::NewGuid().ToString('N') + '.txt')
$sentinel = [Guid]::NewGuid().ToString('N')
[IO.File]::WriteAllText($sentinelPath, $sentinel, [Text.UTF8Encoding]::new($false))
function Assert-DataPreserved {
    if (!(Test-Path -LiteralPath $sentinelPath) -or [IO.File]::ReadAllText($sentinelPath) -ne $sentinel) { throw 'Installer verification user data was changed.' }
}
function Invoke-InstallerProcess([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { throw 'Installer verification process is still running after 60 seconds.' }
    if ($process.ExitCode -ne 0) { throw ('Installer verification process failed with exit code ' + $process.ExitCode) }
}
$uninstaller = Join-Path $installRoot 'unins000.exe'
$commonArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOLAUNCH', '/NOCLOSEAPPLICATIONS', '/NORESTARTAPPLICATIONS')
$verified = $false
try {
    foreach ($phase in @('install', 'reinstall')) {
        $arguments = $commonArguments + @('/LANG=english', ('/DIR="' + $installRoot + '"'), ('/LOG="' + (Join-Path $runRoot ($phase + '.log')) + '"'))
        Invoke-InstallerProcess $installerPath $arguments
        $registration = Read-Installation
        if (!$registration) { throw 'Windows installed-app registration is missing.' }
        if ($registration.DisplayName -ne $appName -or $registration.DisplayVersion -ne $version) { throw 'Installed-app name or version is incorrect.' }
        if (!(Test-SameWindowsPath $registration.InstallLocation $installRoot)) { throw 'Installed-app registration points to another directory.' }
        if (!(Test-Path -LiteralPath (Join-Path $installRoot 'SimpleScraper.exe'))) { throw 'Installed application is missing.' }
        if (!(Test-Path -LiteralPath $uninstaller)) { throw 'Installed application uninstaller is missing.' }
        if (!(Test-Path -LiteralPath $shortcutPath)) { throw 'Start Menu shortcut is missing.' }
        $appPath = Get-ItemProperty -LiteralPath $appPathKey
        if (!(Test-SameWindowsPath $appPath.'(default)' (Join-Path $installRoot 'SimpleScraper.exe'))) { throw 'Windows App Paths registration is incorrect.' }
        $shortcutCopy = Join-Path $runRoot ($phase + '-shortcut.lnk')
        [IO.File]::Copy($shortcutPath, $shortcutCopy, $true)
        $readerOutput = & $windowsPowerShell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -STA -File $shortcutReader -LiteralPath $shortcutCopy
        if ($LASTEXITCODE -ne 0) { throw 'Windows PowerShell STA shortcut inspection failed.' }
        $shortcut = ($readerOutput -join [Environment]::NewLine) | ConvertFrom-Json
        $expectedTarget = Join-Path $installRoot 'SimpleScraper.exe'
        $shortcutReport = [ordered]@{
            phase = $phase
            shortcutPath = $shortcutPath
            copiedShortcutPath = $shortcutCopy
            shortcutBytes = $shortcut.shortcutBytes
            shortcutSha256 = $shortcut.shortcutSha256
            readerPowerShellVersion = $shortcut.powerShellVersion
            readerPowerShellEdition = $shortcut.powerShellEdition
            readerApartmentState = $shortcut.apartmentState
            reader = $shortcut.reader
            actualTarget = $shortcut.targetPath
            actualWorkingDirectory = $shortcut.workingDirectory
            expectedTarget = $expectedTarget
            expectedWorkingDirectory = $installRoot
            targetMatches = Test-SameWindowsPath $shortcut.targetPath $expectedTarget
            workingDirectoryMatches = Test-SameWindowsPath $shortcut.workingDirectory $installRoot
        }
        $shortcutReportPath = Join-Path $runRoot ($phase + '-shortcut.json')
        [IO.File]::WriteAllText($shortcutReportPath, (($shortcutReport | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        if (!$shortcutReport.targetMatches -or !$shortcutReport.workingDirectoryMatches) {
            Write-Output ($shortcutReport | ConvertTo-Json -Compress)
            throw ('Start Menu shortcut target or working directory is incorrect. Details: ' + $shortcutReportPath)
        }
        Assert-DataPreserved
    }
    Invoke-InstallerProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $runRoot 'uninstall.log') + '"'))
    if (Read-Installation) { throw 'Installed-app registration remains after uninstall.' }
    if (Test-Path -LiteralPath $shortcutPath) { throw 'Start Menu shortcut remains after uninstall.' }
    if (Test-Path -LiteralPath $appPathKey) { throw 'Windows App Paths registration remains after uninstall.' }
    if (Test-Path -LiteralPath (Join-Path $installRoot 'SimpleScraper.exe')) { throw 'Application executable remains after uninstall.' }
    Assert-DataPreserved
    $verified = $true
    $report = [ordered]@{
        version = $version
        installer = $installerPath
        installDirectory = $installRoot
        installRegistered = $true
        startMenuShortcutVerified = $true
        reinstallPreservedData = $true
        uninstallRemovedRegistration = $true
        uninstallPreservedData = $true
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'result.json'), (($report | ConvertTo-Json) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    Write-Output ('Installer verification passed: ' + (Join-Path $runRoot 'result.json'))
} finally {
    if (!$verified -and (Test-Path -LiteralPath $uninstaller)) {
        $registration = Read-Installation
        if ($registration -and (Test-SameWindowsPath $registration.InstallLocation $installRoot)) {
            Invoke-InstallerProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
        }
    }
    if (Test-Path -LiteralPath $sentinelPath) { [IO.File]::Delete($sentinelPath) }
}
