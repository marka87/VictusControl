@echo off
net session >nul 2>&1
if errorlevel 1 (
  echo Please run as Administrator: Right-click - Run as Administrator.
  pause
  exit /b 1
)
schtasks /delete /tn "VictusControl" /f >nul 2>&1
schtasks /delete /tn "VictusKbd" /f >nul 2>&1
echo Autostart removed successfully.
pause
