@echo off
rem Update Windows Shell (WinUI): stop the shell and its watchdog, build, start again.
rem The watchdog restarts a shell that dies, so it must be stopped first (--stop), or the
rem build cannot replace the exe. Start goes through the logon task (WSH-99: never run the
rem exe from a packaged app such as Claude Desktop).
setlocal
set "REPO=%~dp0.."
set "PUBLISH=%REPO%\native\publish"
if exist "%PUBLISH%\WindowsShellNative.exe" start "" /wait "%PUBLISH%\WindowsShellNative.exe" --stop
taskkill /im WindowsShellNative.exe /f >nul 2>&1
dotnet publish "%REPO%\native\Shell\Shell.csproj" -c Release -r win-x64 -o "%PUBLISH%"
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
schtasks /run /tn WindowsShellNative >nul 2>&1 || start "" "%PUBLISH%\WindowsShellNative.exe"
