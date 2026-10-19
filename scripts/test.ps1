param([switch]$Integration)
. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try {
    $testDirectory = Join-Path $projectRoot ('.local\test-runs\' + [Guid]::NewGuid().ToString('N'))
    $testMode = '--local'
    if ($Integration) {
        if (-not $env:MINIODESK_TEST_ACCESS_KEY -or -not $env:MINIODESK_TEST_SECRET_KEY) {
            throw 'Define MINIODESK_TEST_ACCESS_KEY y MINIODESK_TEST_SECRET_KEY para tus dos servidores locales.'
        }
        $testMode = '--integration'
    }
    Invoke-Dotnet run --project tests/MinioDesk.Smoke -c Release -- $testMode $testDirectory
} finally { Pop-Location }
