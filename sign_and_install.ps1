$signTool = (Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Filter signtool.exe -Recurse | Where-Object { $_.FullName -match "x64" } | Select-Object -First 1).FullName
$msix = (Get-ChildItem -Path "$PSScriptRoot\src\GhostClawUI.App\AppPackages" -Filter *.msix -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
& $signTool sign /f "$PSScriptRoot\artifacts\cert\GhostClawUI.Dev2.pfx" /p 123456 /fd SHA256 "$msix"
Stop-Process -Name GhostClawUI.App -Force -ErrorAction SilentlyContinue
Stop-Process -Name GhostClawUI.Service -Force -ErrorAction SilentlyContinue
Get-AppxPackage -Name "GhostClawUI" | Remove-AppxPackage -ErrorAction SilentlyContinue
Add-AppxPackage -Path "$msix"
