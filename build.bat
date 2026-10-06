@echo off
cd /d "%~dp0"
echo Stopping running instances...
taskkill /f /im VictusControl.exe >nul 2>&1
taskkill /f /im VictusKbd.exe >nul 2>&1
timeout /t 1 /nobreak >nul

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /out:VictusControl.exe /win32manifest:app.manifest /r:System.dll /r:System.Core.dll /r:System.Management.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll VictusControl.cs
if errorlevel 1 (
  echo.
  echo Compilation ERROR!
  pause
  exit /b 1
)

echo.
echo Success: VictusControl.exe compiled successfully.
echo Starting application...
schtasks /run /tn "VictusControl" >nul 2>&1
if errorlevel 1 (
  start "" "%~dp0VictusControl.exe"
)
pause
