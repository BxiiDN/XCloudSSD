@echo off
rem XCloudSSD portable: installs WinFsp (if missing) and registers XCloudSSDService from this folder.
rem Run as Administrator.
net session >nul 2>&1 || (echo Please run this script as Administrator. & pause & exit /b 1)
cd /d "%~dp0"
reg query "HKLM\SOFTWARE\WOW6432Node\WinFsp" /v InstallDir >nul 2>&1 || (
  echo Installing WinFsp...
  msiexec /i "%~dp0winfsp.msi" /qn /norestart
)
sc stop XCloudSSDService >nul 2>&1
sc config XCloudSSDService binPath= "\"%~dp0XCloudSSDService.exe\"" start= auto >nul 2>&1 || sc create XCloudSSDService binPath= "\"%~dp0XCloudSSDService.exe\"" start= auto DisplayName= "XCloudSSD Service"
sc failure XCloudSSDService reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc start XCloudSSDService
echo Done. Start XCloudSSD.exe to add accounts.
pause
