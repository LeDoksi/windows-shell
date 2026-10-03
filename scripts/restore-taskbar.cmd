@echo off
rem Emergency restore of the Windows taskbar: double-click this file.
rem ASCII only: cmd.exe reads .cmd files in the OEM code page.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore-taskbar.ps1"
