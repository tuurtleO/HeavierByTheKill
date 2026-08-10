@echo off
title Install Heavier by the Kill
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
if errorlevel 1 (
  echo Installation failed. Read the message above for details.
) else (
  echo Installation complete. Start Dark Souls Remastered offline, load a character,
  echo then run START MOD.cmd from this folder.
)
echo.
pause
