@echo off
chcp 936 >nul
rem ============================================================================
rem  IPRC1000 Adapter - one-click deployment entry (double-click this file)
rem
rem  Why this file exists:
rem    tools\one-click-deploy.ps1 must run as Administrator, but the shell's
rem    "Run with PowerShell" context menu item is NOT elevated. Asking the user
rem    to open an elevated shell and cd into the folder is error-prone.
rem    This wrapper switches to its own folder, runs the ps1 with Bypass policy
rem    (the ps1 elevates itself and shows the UAC prompt), then keeps the window
rem    open so the result stays readable.
rem
rem  Note: the ps1 is passed as a RELATIVE path on purpose. Putting a Chinese
rem  absolute path on the command line can be mangled by the ANSI code page.
rem  Keep this file ASCII-only so its encoding never matters.
rem ============================================================================
cd /d "%~dp0"

if not exist "tools\一键部署.ps1" (
  echo [ERROR] tools\one-click-deploy.ps1 is missing.
  echo         Keep this .cmd in the package root, next to the tools folder.
  echo.
  pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "tools\一键部署.ps1"

echo.
echo [done] Deployment script finished. If anything failed above,
echo        please keep a screenshot of this window.
pause >nul
