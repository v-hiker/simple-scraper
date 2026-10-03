param([ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Resolve-ProjectSdk
& $sdk restore (Join-Path $repository 'SimpleScraper.slnx') --runtime $RuntimeIdentifier --locked-mode '-p:Platform=x64' -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Locked dependency restore failed.' }
