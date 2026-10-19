. "$PSScriptRoot/common.ps1"
Push-Location $projectRoot
try { Invoke-Dotnet run --project src/MinioDesk.App -c Release }
finally { Pop-Location }
