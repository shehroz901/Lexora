@echo off
rem Downloads the offline engines (~3 GB) into app\engine. Run once, from the repo folder.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-engine.ps1" %*
pause
