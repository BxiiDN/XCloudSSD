@echo off
rem Stops and removes XCloudSSDService (pending uploads are committed on stop). WinFsp stays installed.
net session >nul 2>&1 || (echo Please run this script as Administrator. & pause & exit /b 1)
sc stop XCloudSSDService
timeout /t 5 /nobreak >nul
sc delete XCloudSSDService
pause
