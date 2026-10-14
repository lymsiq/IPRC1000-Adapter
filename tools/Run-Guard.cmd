@echo off
chcp 65001 >nul
cd /d "%~dp0"
net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -Command "Start-Process cmd -ArgumentList '/c','\"%~f0\"' -Verb RunAs"
  exit /b
)
where python >nul 2>&1
if %errorlevel% neq 0 (
  echo [X] 未找到 python，请先安装 Python 3
  pause & exit /b 1
)
python "%~dp0stuck_key_guard.py"
pause
