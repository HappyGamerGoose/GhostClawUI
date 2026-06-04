@echo off
title GhostClawUI Package Upgrader
setlocal
cd /d "%~dp0"

echo ============================================================
echo   GhostClawUI Version 2.0.0.0 Upgrade ^& Sideload Utility
echo ============================================================
echo.
echo [1/3] Stopping locked background service...
sc.exe stop GhostClawUI.AgentService > nul 2>&1
taskkill /F /IM GhostClawUI.Service.exe > nul 2>&1
timeout /t 2 /nobreak > nul

echo.
echo [2/3] Importing certificate system-wide to Local Machine...
set /p PFX_PASSWORD=Enter Certificate Password: 
if "%PFX_PASSWORD%"=="" (
    echo Password is required to import the certificate.
    pause > nul
    exit /b 1
)

powershell -Command "$password = ConvertTo-SecureString '%PFX_PASSWORD%' -AsPlainText -Force; Import-PfxCertificate -FilePath 'artifacts\cert\GhostClawUI.Dev.pfx' -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' -Password $password"

echo.
echo [2.5/3] Signing MSIX Package (if needed)...
for /f "delims=" %%i in ('powershell -Command "(Get-ChildItem -Path \"$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\" -Filter \"signtool.exe\" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName"') do set SIGNTOOL_PATH=%%i
if defined SIGNTOOL_PATH (
    "%SIGNTOOL_PATH%" sign /f "artifacts\cert\GhostClawUI.Dev.pfx" /p %PFX_PASSWORD% /fd SHA256 "src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix"
) else (
    echo signtool.exe not found in NuGet packages. Skipping signing...
)

echo.
echo [3/3] Upgrading MSIX Package to version 2.0.0.0...
powershell -Command "Get-AppxPackage *GhostClawUI* | Remove-AppxPackage -Verbose"
powershell -Command "Add-AppxPackage -Path 'src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix' -Verbose"

echo.
echo ============================================================
echo   Upgrade completed successfully! Press any key to exit.
echo ============================================================
pause > nul
