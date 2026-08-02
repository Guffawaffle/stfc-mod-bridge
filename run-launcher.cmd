@echo off
setlocal

set "REPO_ROOT=%~dp0"
set "LAUNCHER_PROJECT=%REPO_ROOT%src\STFCCommunityMod.Launcher\STFCCommunityMod.Launcher.csproj"
set "LAUNCHER_DIRECTORY=%REPO_ROOT%src\STFCCommunityMod.Launcher\bin\Release\net8.0-windows\win-x64"
set "LAUNCHER_EXE=%LAUNCHER_DIRECTORY%\STFCCommunityMod.Launcher.exe"

if not defined WINDIR (
  if not defined SystemRoot (
    echo Neither WINDIR nor SystemRoot is available in this shell.
    goto :failure
  )
  set "WINDIR=%SystemRoot%"
)

echo Building the latest launcher from this checkout...
dotnet build "%LAUNCHER_PROJECT%" -c Release -r win-x64 --self-contained true --nologo
if errorlevel 1 goto :failure

if not exist "%LAUNCHER_EXE%" (
  echo.
  echo Build completed, but the launcher executable was not found:
  echo   %LAUNCHER_EXE%
  goto :failure
)

echo Starting:
echo   %LAUNCHER_EXE%
start "" /D "%LAUNCHER_DIRECTORY%" "%LAUNCHER_EXE%"
exit /b 0

:failure
echo.
echo The launcher was not started. Review the build output above.
pause
exit /b 1
