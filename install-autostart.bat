@echo off
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Please run as Administrator: Right-click - Run as Administrator.
  pause
  exit /b 1
)

echo Registering VictusControl in Windows Task Scheduler...
powershell -NoProfile -ExecutionPolicy Bypass -Command "$app = Join-Path (Get-Location).Path 'VictusControl.exe'; $dir = (Get-Location).Path; $action = New-ScheduledTaskAction -Execute $app -WorkingDirectory $dir; $trigger = New-ScheduledTaskTrigger -AtLogOn; $trigger.Delay = 'PT5S'; $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds 0); $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest; Register-ScheduledTask -TaskName 'VictusControl' -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null"

if errorlevel 1 (
  echo Failed to configure scheduled task.
  pause
  exit /b 1
)

echo.
echo Autostart configured successfully!
echo - Starts 5 seconds after logon (allows system tray to load)
echo - Allowed to start and keep running on battery
echo - Configured to run with highest administrator privileges
pause
