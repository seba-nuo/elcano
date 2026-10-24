. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try { Invoke-Dotnet run --project src/Elcano.App -c Release }
finally { Pop-Location }
