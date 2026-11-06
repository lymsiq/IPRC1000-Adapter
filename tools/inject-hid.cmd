@echo off
chcp 936 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0inject-hid-service.ps1" -Mac 20c047f313d6 -Apply
