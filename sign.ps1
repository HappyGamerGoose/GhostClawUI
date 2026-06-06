$scriptDir = $PSScriptRoot
if (-not $scriptDir) { $scriptDir = "." }

$pfxPath = Join-Path $scriptDir "artifacts\cert\GhostClawUI.Dev2.pfx"
$msixPath = Join-Path $scriptDir "src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix"
$signtool = Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match "x64" } | Select-Object -First 1 | Select-Object -ExpandProperty FullName

if (-not $signtool) {
    Write-Warning "signtool.exe not found in NuGet cache."
    exit
}

$plainPass = "123456"

Write-Host "Signing MSIX package: $msixPath using SignTool"
& $signtool sign /f $pfxPath /p $plainPass /fd SHA256 $msixPath
