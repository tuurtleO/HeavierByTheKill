@echo off
title Heavier by the Kill
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-mod.ps1"
echo.
echo The mod has stopped. Press any key to close this window.
pause >nul
