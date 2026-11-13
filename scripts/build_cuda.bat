@echo off
REM Entry point for scripts\build_cuda.ps1 (ASCII only; all messages live in the .ps1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build_cuda.ps1" %*
exit /b %ERRORLEVEL%
