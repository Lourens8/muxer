# Run this script as Administrator
#Requires -RunAsAdministrator

$ErrorActionPreference = "Stop"

Write-Host "=== Muxer SSH Server Setup ===" -ForegroundColor Cyan
Write-Host ""

# 1. Install OpenSSH Server
Write-Host "[1/5] Installing OpenSSH Server..." -ForegroundColor Yellow
$sshCapability = Get-WindowsCapability -Online | Where-Object Name -like 'OpenSSH.Server*'
if ($sshCapability.State -ne 'Installed') {
    Add-WindowsCapability -Online -Name $sshCapability.Name
    Write-Host "  Installed." -ForegroundColor Green
} else {
    Write-Host "  Already installed." -ForegroundColor Green
}

# 2. Configure sshd_config
Write-Host "[2/5] Configuring sshd..." -ForegroundColor Yellow
$sshdConfig = "C:\ProgramData\ssh\sshd_config"

# Read current config
$config = Get-Content $sshdConfig -Raw

# Ensure password authentication is enabled
$config = $config -replace '#?PasswordAuthentication\s+\w+', 'PasswordAuthentication yes'

# Set default shell to our muxer-shell script
$shellPath = "C:\projects\muxer\scripts\muxer-shell.ps1"

# Write config
Set-Content $sshdConfig $config
Write-Host "  Password auth enabled." -ForegroundColor Green

# Set default shell via registry
$regPath = "HKLM:\SOFTWARE\OpenSSH"
if (-not (Test-Path $regPath)) {
    New-Item -Path $regPath -Force | Out-Null
}
New-ItemProperty -Path $regPath -Name DefaultShell -Value "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -PropertyType String -Force | Out-Null
New-ItemProperty -Path $regPath -Name DefaultShellCommandOption -Value "-NoLogo -NoProfile -ExecutionPolicy Bypass -File $shellPath" -PropertyType String -Force | Out-Null
Write-Host "  Default shell set to muxer-shell." -ForegroundColor Green

# 3. Firewall rule
Write-Host "[3/5] Configuring firewall..." -ForegroundColor Yellow
$rule = Get-NetFirewallRule -DisplayName "Muxer SSH" -ErrorAction SilentlyContinue
if (-not $rule) {
    New-NetFirewallRule -DisplayName "Muxer SSH" -Direction Inbound -Action Allow -Protocol TCP -LocalPort 22 | Out-Null
    Write-Host "  Firewall rule added for port 22." -ForegroundColor Green
} else {
    Write-Host "  Firewall rule already exists." -ForegroundColor Green
}

# 4. Start service
Write-Host "[4/5] Starting SSH service..." -ForegroundColor Yellow
Set-Service -Name sshd -StartupType Automatic
Start-Service sshd
Write-Host "  sshd started and set to auto-start." -ForegroundColor Green

# 5. Verify
Write-Host "[5/5] Verifying..." -ForegroundColor Yellow
$svc = Get-Service sshd
if ($svc.Status -eq 'Running') {
    Write-Host ""
    Write-Host "  SSH server is running on port 22." -ForegroundColor Green
    Write-Host "  Connect with: ssh loure@192.168.0.65" -ForegroundColor White
    Write-Host "  Password: your Windows login password" -ForegroundColor White
    Write-Host ""
} else {
    Write-Host "  WARNING: sshd is not running!" -ForegroundColor Red
}
