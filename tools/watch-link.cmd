@echo off
chcp 936 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0watch-link.ps1" -Seconds 60
