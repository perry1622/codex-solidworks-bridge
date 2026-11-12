@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-CodexBridge.ps1" -AllowWrite
if errorlevel 1 echo Installation failed. Review the message above.
pause
