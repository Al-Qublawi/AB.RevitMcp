@echo off
setlocal
echo.
echo   AB Revit MCP Bridge - uninstaller
echo   ================================
echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\uninstall.ps1" %*
echo.
pause
exit /b %ERRORLEVEL%
