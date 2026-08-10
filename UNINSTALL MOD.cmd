@echo off
title Uninstall Heavier by the Kill
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
if errorlevel 1 echo Uninstall failed. Read the message above for details.
echo.
pause
