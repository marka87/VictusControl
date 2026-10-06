@echo off
net session >nul 2>&1
if errorlevel 1 (
  echo Bitte als Administrator ausfuehren: Rechtsklick - Als Administrator ausfuehren.
  pause
  exit /b 1
)
schtasks /delete /tn "VictusControl" /f >nul 2>&1
schtasks /delete /tn "VictusKbd" /f >nul 2>&1
echo Autostart entfernt.
pause
