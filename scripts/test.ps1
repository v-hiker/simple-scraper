param(
    [switch]$VerboseChecks,
    [switch]$SkipRestore,
    [ValidateSet('win-x64')][string]$RuntimeIdentifier = 'win-x64'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'sdk.ps1')
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = Resolve-ProjectSdk
if (!$SkipRestore) { & (Join-Path $PSScriptRoot 'restore.ps1') -RuntimeIdentifier $RuntimeIdentifier }
$project = Join-Path $repository 'tests/SimpleScraper.Tests/SimpleScraper.Tests.csproj'
$arguments = @($repository)
if ($VerboseChecks) { $arguments += '--verbose' }
& $sdk run --project $project --runtime $RuntimeIdentifier '-p:Platform=x64' --no-restore -- @arguments
if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
$projectionProject = Join-Path $repository 'tests/SimpleScraper.Performance/SimpleScraper.Performance.csproj'
& $sdk run --project $projectionProject --runtime $RuntimeIdentifier '-p:Platform=x64' --no-restore -- --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Projection equivalence regression failed.' }
