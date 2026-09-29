@echo off
rem Compile et teste PCBoost (Release x64). Journaux : artifacts\logs
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
echo Code de sortie : %ERRORLEVEL% - journaux dans artifacts\logs
pause
