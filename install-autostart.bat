@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Please run as Administrator: Right-click - Run as Administrator.
  pause
  exit /b 1
)
schtasks /create /tn "VictusControl" /tr "\"%~dp0VictusControl.exe\"" /sc onlogon /rl highest /f
echo.
echo Autostart configured successfully. Do not move or rename this folder.
pause
