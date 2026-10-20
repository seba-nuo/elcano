param(
    [string[]]$Endpoints = @('http://localhost:9000', 'http://localhost:9100'),
    [string]$Bucket = 'miniodesk-demo',
    [string]$Region = 'us-east-1',
    [switch]$UseLocalLabCredentials,
    [ValidateRange(0,2000)][int]$PaginationFiles = 0
)
. "$PSScriptRoot/common.ps1"
$previousAccess = $env:MINIODESK_SAMPLE_ACCESS_KEY
$previousSecret = $env:MINIODESK_SAMPLE_SECRET_KEY
Push-Location $projectRoot
try {
    if ($UseLocalLabCredentials) {
        $labConfig = Join-Path $projectRoot '.local\minio-lab\.env'
        if (-not (Test-Path -LiteralPath $labConfig)) { throw 'No se encontró el archivo .env del laboratorio local.' }
        foreach ($line in Get-Content -LiteralPath $labConfig) {
            if ($line -match '^\s*MINIO_ROOT_USER=(.*)$') { $env:MINIODESK_SAMPLE_ACCESS_KEY = $Matches[1].Trim().Trim('"', "'") }
            if ($line -match '^\s*MINIO_ROOT_PASSWORD=(.*)$') { $env:MINIODESK_SAMPLE_SECRET_KEY = $Matches[1].Trim().Trim('"', "'") }
        }
    }
    if (-not $env:MINIODESK_SAMPLE_ACCESS_KEY -or -not $env:MINIODESK_SAMPLE_SECRET_KEY) {
        throw 'Define MINIODESK_SAMPLE_ACCESS_KEY y MINIODESK_SAMPLE_SECRET_KEY o usa -UseLocalLabCredentials.'
    }
    $sampleDirectory = Join-Path $projectRoot ('.local\sample-data\' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet run --project tools/MinioDesk.Samples -c Release -- $sampleDirectory $Bucket $Region @Endpoints --pagination $PaginationFiles
} finally {
    $env:MINIODESK_SAMPLE_ACCESS_KEY = $previousAccess
    $env:MINIODESK_SAMPLE_SECRET_KEY = $previousSecret
    Pop-Location
}
