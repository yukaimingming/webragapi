@echo off
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0GenerateUpdateSha256.ps1" %*
echo.
pause
