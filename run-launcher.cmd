@echo off
setlocal
pwsh -NoLogo -NoProfile -File "%~dp0scripts\run-launcher.ps1" %*
if errorlevel 1 goto :failure
exit /b 0
:failure
echo.
echo The launcher was not started. Review the build output above.
pause
exit /b 1
