@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Bitte als Administrator ausfuehren: Rechtsklick - Als Administrator ausfuehren.
  pause
  exit /b 1
)
schtasks /create /tn "VictusControl" /tr "\"%~dp0VictusControl.exe\"" /sc onlogon /rl highest /f
echo.
echo Autostart eingerichtet. Der Ordner darf jetzt nicht mehr verschoben werden.
pause
