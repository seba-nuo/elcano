param([Parameter(Mandatory)][string]$PublishedDirectory, [switch]$SkipExtension)
$ErrorActionPreference = 'Stop'
$legalProjectRoot = Split-Path -Parent $PSScriptRoot
$published = [IO.Path]::GetFullPath($PublishedDirectory)
$licenseRoot = Join-Path $legalProjectRoot 'licenses'
$manifest = Get-Content -LiteralPath (Join-Path $licenseRoot 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1) { throw 'Formato de inventario de licencias no compatible.' }
$reviewedPackages = @{}
foreach ($package in $manifest.packages) { $reviewedPackages[$package.id] = $package.version }
$reviewedRuntimes = @{}
foreach ($runtime in $manifest.runtimes) { $reviewedRuntimes[$runtime.id] = $runtime.version }
$reviewedFiles = @{}
foreach ($file in $manifest.files) {
    $reviewedFiles[$file.path] = $true
    foreach ($basePath in @($licenseRoot, (Join-Path $published 'licenses'))) {
        $filePath = Join-Path $basePath $file.path
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Falta un documento de licencia: $filePath" }
        if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Cambió el documento de licencia sin revisar el inventario: $filePath"
        }
    }
}
foreach ($component in @($manifest.packages) + @($manifest.runtimes)) {
    foreach ($relative in $component.files) {
        if (-not $reviewedFiles.ContainsKey($relative)) { throw "Aviso no inventariado para $($component.id): $relative" }
    }
}
foreach ($relative in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/manifest.json', 'licenses/README.md')) {
    $sourceHash = (Get-FileHash -LiteralPath (Join-Path $legalProjectRoot $relative) -Algorithm SHA256).Hash
    $targetHash = (Get-FileHash -LiteralPath (Join-Path $published $relative) -Algorithm SHA256).Hash
    if ($sourceHash -ne $targetHash) { throw "El portable no conserva el documento original: $relative" }
}
foreach ($lockFile in @('src/Elcano.App/packages.lock.json', 'src/Elcano.Core/packages.lock.json')) {
    $lock = Get-Content -LiteralPath (Join-Path $legalProjectRoot $lockFile) -Raw | ConvertFrom-Json
    foreach ($framework in $lock.dependencies.PSObject.Properties) {
        foreach ($dependency in $framework.Value.PSObject.Properties) {
            if ($dependency.Value.type -eq 'Project') { continue }
            if ($reviewedPackages[$dependency.Name] -ne $dependency.Value.resolved) {
                throw "Dependencia sin revisión de licencias: $($dependency.Name) $($dependency.Value.resolved)"
            }
        }
    }
}
$dependencies = Get-Content -LiteralPath (Join-Path $published 'Elcano.deps.json') -Raw | ConvertFrom-Json
foreach ($library in $dependencies.libraries.PSObject.Properties) {
    $parts = $library.Name -split '/', 2
    if ($library.Value.type -eq 'package' -and $reviewedPackages[$parts[0]] -ne $parts[1]) {
        throw "Paquete publicado sin revisión de licencias: $($library.Name)"
    }
    if ($library.Value.type -eq 'runtimepack') {
        $id = $parts[0] -replace '^runtimepack\.', ''
        if ($reviewedRuntimes[$id] -ne $parts[1]) { throw "Runtime publicado sin revisión de licencias: $($library.Name)" }
    }
}
foreach ($binary in $manifest.native) {
    $filePath = Join-Path $published $binary.path
    if ($SkipExtension -and $binary.path -eq 'extensions/httpfs.duckdb_extension' -and -not (Test-Path -LiteralPath $filePath)) { continue }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binary.sha256) {
        throw "Binario nativo distinto del revisado: $($binary.path). Revisa sus avisos antes de publicar."
    }
}
Write-Output "Licencias verificadas: $($manifest.files.Count) documentos, dependencias .NET y binarios nativos."
