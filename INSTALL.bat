@echo off
REM ---------------------------------------------------------------------------
REM  AB Revit MCP Bridge - double-click installer
REM
REM  Runs the PowerShell installer with an execution-policy bypass scoped to
REM  this process only. Nothing about the machine's policy is changed.
REM
REM  Close Revit before running: add-ins load only at startup, and running
REM  Revit locks the files being replaced.
REM ---------------------------------------------------------------------------
setlocal
echo.
echo   AB Revit MCP Bridge - installer
echo   ==============================
echo.

where powershell >nul 2>&1
if errorlevel 1 (
    echo   ERROR: Windows PowerShell was not found on this system.
    echo.
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\install.ps1" -ConfigureClients %*
set RC=%ERRORLEVEL%

echo.
if "%RC%"=="0" (
    echo   Installation finished.
) else (
    echo   Installation reported problems ^(exit code %RC%^). See the output above.
)
echo.
pause
exit /b %RC%
