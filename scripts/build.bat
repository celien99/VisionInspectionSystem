@echo off
REM Inspection / Desktop build entry point (Windows PowerShell 5.1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
