param([switch]$Integration, [switch]$FolderProtocol, [switch]$Ui, [switch]$NoBuild)
. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try {
    if (([int]$Integration.IsPresent + [int]$FolderProtocol.IsPresent + [int]$Ui.IsPresent) -gt 1) { throw 'Elige un solo modo: -Integration, -FolderProtocol o -Ui.' }
    $testDirectory = Join-Path $projectRoot ('.local\test-runs\' + [Guid]::NewGuid().ToString('N'))
    $testMode = '--local'
    if ($Integration) {
        if (-not $env:MINIODESK_TEST_ACCESS_KEY -or -not $env:MINIODESK_TEST_SECRET_KEY) {
            throw 'Define MINIODESK_TEST_ACCESS_KEY y MINIODESK_TEST_SECRET_KEY para tus dos servidores locales.'
        }
        $testMode = '--integration'
    }
    if ($FolderProtocol) { $testMode = '--folder-protocol' }
    $testProject = 'MinioDesk.Smoke'
    if ($Ui) { $testProject = 'MinioDesk.UiTests'; $testDirectory = Join-Path $projectRoot 'TestResults\UI' }
    if (-not $NoBuild) { Invoke-Dotnet build "tests/$testProject" -c Release --nologo }
    $assemblyPath = Join-Path $projectRoot "tests/$testProject/bin/Release/net10.0-windows/win-x64/$testProject.dll"
    if ($Ui) { Invoke-Dotnet $assemblyPath $testDirectory }
    else { Invoke-Dotnet $assemblyPath $testMode $testDirectory }
} finally { Pop-Location }
