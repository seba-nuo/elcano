param([switch]$SkipExtension, [switch]$NoRestore, [string]$OutputDirectory = '', [string]$PackageVersion = '')
. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try {
    $destination = Join-Path $projectRoot 'artifacts\Elcano-win-x64'
    if ($OutputDirectory) { $destination = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $projectRoot $OutputDirectory })) }
    if (@(Get-Process -Name Elcano -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $destination 'Elcano.exe') }).Count -gt 0) {
        throw 'La publicación está abierta. Cierra esa instancia o indica otra carpeta con -OutputDirectory.'
    }
    if (-not $SkipExtension) {
        $extensionCache = Join-Path $projectRoot '.local\publish-cache'
        if ($NoRestore) {
            Invoke-Dotnet (Join-Path $projectRoot 'tests/Elcano.Smoke/bin/Release/net10.0-windows/win-x64/Elcano.Smoke.dll') --prepare-extensions $extensionCache
        } else { Invoke-Dotnet run --project tests/Elcano.Smoke -c Release -- --prepare-extensions $extensionCache }
        $extensionFilePath = Get-Content -LiteralPath (Join-Path $extensionCache 'extension-path.txt') -Raw
        if (-not (Test-Path -LiteralPath $extensionFilePath)) { throw 'No se encontró httpfs después de prepararlo.' }
        $extensionOutput = Join-Path $projectRoot '.local\runtime-extensions'
        New-Item -ItemType Directory -Force $extensionOutput | Out-Null
        Copy-Item -LiteralPath $extensionFilePath -Destination (Join-Path $extensionOutput 'httpfs.duckdb_extension') -Force
    }
    $publishArguments = @('publish', 'src/Elcano.App', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $destination, '-p:PublishSingleFile=false', '-m:1', '-nr:false')
    if ($NoRestore) { $publishArguments += '--no-restore' }
    if ($PackageVersion) {
        if ($PackageVersion -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'PackageVersion debe tener formato semántico, como 0.2.0.' }
        $publishArguments += "-p:Version=$PackageVersion"
    }
    Invoke-Dotnet @publishArguments
    if (-not $SkipExtension -and -not (Test-Path -LiteralPath (Join-Path $destination 'extensions/httpfs.duckdb_extension'))) { throw 'La publicación no contiene httpfs.' }
    & "$PSScriptRoot/verify-licenses.ps1" -PublishedDirectory $destination -SkipExtension:$SkipExtension
    $zipName = if ($PackageVersion) { "Elcano-$PackageVersion-win-x64.zip" } else { 'Elcano-win-x64.zip' }
    $zipPath = Join-Path (Split-Path -Parent $destination) $zipName
    $temporaryZip = $zipPath + '.' + [Guid]::NewGuid().ToString('N') + '.part'
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($destination, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
        Move-Item -LiteralPath $temporaryZip -Destination $zipPath -Force
    } finally { if (Test-Path -LiteralPath $temporaryZip) { Remove-Item -LiteralPath $temporaryZip } }
    $hashValue = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($zipPath + '.sha256') -Value "$hashValue  $zipName" -Encoding ascii
    Write-Output "Aplicación lista: $destination\Elcano.exe"
    Write-Output 'Distribuye la carpeta completa; incluye el runtime .NET y las bibliotecas nativas.'
    Write-Output "ZIP: $zipPath"
} finally { Pop-Location }
