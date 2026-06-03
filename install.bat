@echo off
title GhostClawUI Package Upgrader
echo ============================================================
echo   GhostClawUI Version 2.0.0.0 Upgrade & Sideload Utility
echo ============================================================
echo.
echo [1/3] Stopping locked background service...
sc.exe stop GhostClawUI.AgentService
taskkill /F /IM GhostClawUI.Service.exe
timeout /t 2 /nobreak > nul

echo.
echo [2/3] Importing certificate system-wide to Local Machine...
powershell -Command "$password = ConvertTo-SecureString 'ghostclaw' -AsPlainText -Force; Import-PfxCertificate -FilePath 'C:\Users\akshi\Documents\GhostClawUI\artifacts\cert\GhostClawUI.Dev.pfx' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $password"

echo.
echo [2.5/3] Signing MSIX Package...
powershell -Command "& 'C:\Users\akshi\.nuget\packages\microsoft.windows.sdk.buildtools\10.0.28000.1839\bin\10.0.28000.0\x64\signtool.exe' sign /f 'C:\Users\akshi\Documents\GhostClawUI\artifacts\cert\GhostClawUI.Dev.pfx' /p ghostclaw /fd SHA256 'C:\Users\akshi\Documents\GhostClawUI\src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix'"

echo.
echo [3/3] Upgrading MSIX Package to version 2.0.0.0...
powershell -Command "Get-AppxPackage *GhostClawUI* | Remove-AppxPackage -Verbose"
powershell -Command "Add-AppxPackage -Path 'C:\Users\akshi\Documents\GhostClawUI\src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix' -Verbose"

echo.
echo ============================================================
echo   Upgrade completed successfully! Press any key to exit.
echo ============================================================
pause > nul
