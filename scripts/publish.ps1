param([switch]$SkipExtension)
. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try {
    $destination = Join-Path $projectRoot 'artifacts\MinioDesk-win-x64'
    if (-not $SkipExtension) {
        $extensionCache = Join-Path $projectRoot '.local\publish-cache'
        Invoke-Dotnet run --project tests/MinioDesk.Smoke -c Release -- --prepare-extensions $extensionCache
        $extensionFilePath = Get-Content -LiteralPath (Join-Path $extensionCache 'extension-path.txt') -Raw
        if (-not (Test-Path -LiteralPath $extensionFilePath)) { throw 'No se encontró httpfs después de prepararlo.' }
        $extensionOutput = Join-Path $projectRoot '.local\runtime-extensions'
        New-Item -ItemType Directory -Force $extensionOutput | Out-Null
        Copy-Item -LiteralPath $extensionFilePath -Destination (Join-Path $extensionOutput 'httpfs.duckdb_extension') -Force
    }
    Invoke-Dotnet publish src/MinioDesk.App -c Release -r win-x64 --self-contained true -o $destination '-p:PublishSingleFile=false'
    Write-Output "Aplicación lista: $destination\MinioDesk.exe"
    Write-Output 'Distribuye la carpeta completa; incluye el runtime .NET y las bibliotecas nativas.'
} finally { Pop-Location }
