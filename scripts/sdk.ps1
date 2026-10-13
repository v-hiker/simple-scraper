function Resolve-ProjectSdk {
    $portable = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../.tools/dotnet/dotnet.exe'))
    if (Test-Path -LiteralPath $portable) { return $portable }
    $installed = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($installed) { return $installed.Source }
    throw 'Install .NET 10 SDK before building.'
}
