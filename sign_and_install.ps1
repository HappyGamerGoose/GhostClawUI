$signTool = (Get-ChildItem -Path "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Filter signtool.exe -Recurse | Where-Object { $_.FullName -match "x64" } | Select-Object -First 1).FullName
& $signTool sign /f artifacts\cert\GhostClawUI.Dev2.pfx /p 123456 /fd SHA256 src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix
Remove-AppxPackage -Package GhostClawUI_2.0.0.0_x64__0694peptf573c -ErrorAction SilentlyContinue
Add-AppxPackage -Path src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix
