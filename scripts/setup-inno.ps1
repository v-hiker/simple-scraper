param([string]$ToolsDirectory = 'artifacts/tools/inno-setup')
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $repository 'artifacts'
$destination = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($ToolsDirectory)) { $ToolsDirectory } else { Join-Path $repository $ToolsDirectory }))
if (!$destination.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Inno Setup tools must stay inside the repository artifacts directory.' }
$ancestor = $destination
while ($ancestor.Length -ge $artifactsRoot.Length) {
    if ((Test-Path -LiteralPath $ancestor) -and (((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw 'Inno Setup tools cannot traverse a symbolic link or junction.' }
    if ($ancestor.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
    $ancestor = Split-Path -Parent $ancestor
}
$compiler = Join-Path $destination 'ISCC.exe'
$versionMarker = Join-Path $destination 'compiler-version.txt'
if ((Test-Path -LiteralPath $compiler) -and (Test-Path -LiteralPath $versionMarker) -and ([IO.File]::ReadAllText($versionMarker).Trim() -eq '6.7.3')) {
    Write-Output ('Compiler: ' + $compiler)
    return
}
$downloadRoot = Join-Path $artifactsRoot 'tools/downloads'
[IO.Directory]::CreateDirectory($downloadRoot) | Out-Null
$installer = Join-Path $downloadRoot 'innosetup-6.7.3.exe'
$expectedHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
if (!(Test-Path -LiteralPath $installer) -or ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash)) {
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
}
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'Inno Setup download SHA-256 verification failed.' }
$signature = Get-AuthenticodeSignature -FilePath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Pyrsys B\.V\.(?:,|$)') { throw 'Inno Setup publisher signature verification failed.' }
$arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $destination + '"'))
$process = Start-Process -FilePath $installer -ArgumentList $arguments -WindowStyle Hidden -PassThru
if (!$process.WaitForExit(60000)) { throw 'Inno Setup installation is still running after 60 seconds.' }
if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw ('Inno Setup compiler installation failed: ' + $process.ExitCode) }
[IO.File]::WriteAllText($versionMarker, ('6.7.3' + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Output ('Compiler: ' + $compiler)
