$pfxPath = "C:\Users\akshi\Documents\GhostClawUI\artifacts\cert\GhostClawUI.Dev.pfx"
$msixPath = "C:\Users\akshi\Documents\GhostClawUI\src\GhostClawUI.App\AppPackages\GhostClawUI.App_1.9.65.0_x64_Debug_Test\GhostClawUI.App_1.9.65.0_x64_Debug.msix"
$signtool = "C:\Users\akshi\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.26100.7705\bin\10.0.26100.0\x64\signtool.exe"

Write-Host "Signing MSIX package: $msixPath using SignTool"
& $signtool sign /f $pfxPath /p ghostclaw /fd SHA256 $msixPath

