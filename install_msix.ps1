$logPath = "C:\Users\akshi\Documents\GhostClawUI\install_log.txt"
Start-Transcript -Path $logPath -Append

try {
    Write-Host "============================================================"
    Write-Host "  GhostClawUI Version 1.9.51.0 Upgrade & Sideload Utility"
    Write-Host "============================================================"
    Write-Host ""

    Write-Host "[1/3] Stopping background service and active processes..."
    # Stop background service if it exists and is running
    $svc = Get-Service -Name "GhostClawUI.AgentService" -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -eq "Running") {
        Stop-Service -Name "GhostClawUI.AgentService" -Force -Verbose
    }
    
    # Kill any active processes to release locks
    Stop-Process -Name "GhostClawUI.Service", "GhostClawUI.App", "node", "python" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    $msixPath = "C:\Users\akshi\Documents\GhostClawUI\src\GhostClawUI.App\AppPackages\GhostClawUI.App_2.0.0.0_x64_Test\GhostClawUI.App_2.0.0.0_x64.msix"

    Write-Host "[2/3] Registering certificate for current user..."
    try {
        $password = ConvertTo-SecureString "ghostclaw" -AsPlainText -Force
        Import-PfxCertificate -FilePath "C:\Users\akshi\Documents\GhostClawUI\artifacts\cert\GhostClawUI.Dev.pfx" -CertStoreLocation "Cert:\CurrentUser\TrustedPeople" -Password $password
    } catch {
        Write-Host "Warning: Certificate registration skipped or failed, continuing deployment: $_"
    }

    Write-Host "[3/3] Uninstalling existing app package..."
    Get-AppxPackage *GhostClawUI* | Remove-AppxPackage -Verbose

    Write-Host "Installing package: $msixPath"
    Add-AppxPackage -Path $msixPath -Verbose

    Write-Host ""
    Write-Host "============================================================"
    Write-Host "  Upgrade completed successfully!"
    Write-Host "============================================================"
} catch {
    Write-Warning $_.Exception.Message
} finally {
    Stop-Transcript
}
