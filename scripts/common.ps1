$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.local\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetExe = $localDotnet
    $env:DOTNET_ROOT = Split-Path -Parent $localDotnet
} else {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCommand) { throw 'Instala el SDK de .NET 10 para Windows.' }
    $dotnetExe = $dotnetCommand.Source
}
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.local\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.local\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
function Invoke-Dotnet {
    & $dotnetExe @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet terminó con código $LASTEXITCODE" }
}
