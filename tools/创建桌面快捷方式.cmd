@echo off
setlocal
set "HERE=%~dp0"
rem 本文件放在程序目录里、或它的 tools 子目录里都能用
set "EXE=%HERE%IPRC1000 Adapter.exe"
if not exist "%EXE%" set "EXE=%HERE%..\IPRC1000 Adapter.exe"
if not exist "%EXE%" (
  echo.
  echo   没找到 IPRC1000 Adapter.exe
  echo   请把本文件放在程序目录里（或它的 tools 子目录）再运行。
  echo.
  pause
  exit /b 1
)
for %%I in ("%EXE%") do set "DIR=%%~dpI"
if "%DIR:~-1%"=="\" set "DIR=%DIR:~0,-1%"
set "IPRC_EXE=%EXE%"
set "IPRC_DIR=%DIR%"
set "PS=$w=New-Object -ComObject WScript.Shell;$d=[Environment]::GetFolderPath('Desktop');$p=Join-Path $d 'IPRC1000 遥控器.lnk';$s=$w.CreateShortcut($p);$s.TargetPath=$env:IPRC_EXE;$s.WorkingDirectory=$env:IPRC_DIR;$s.IconLocation=$env:IPRC_EXE+',0';$s.Description='IPRC1000 遥控器适配器';$s.Save();$b=[IO.File]::ReadAllBytes($p);$b[0x15]=$b[0x15] -bor 0x20;[IO.File]::WriteAllBytes($p,$b);Write-Host ('已创建: '+$p)"
powershell -NoProfile -ExecutionPolicy Bypass -Command "%PS%"
if errorlevel 1 (
  echo.
  echo   创建失败 —— 请右键本文件选「以管理员身份运行」后重试。
)
echo.
echo   桌面已生成「IPRC1000 遥控器」，并已设为「以管理员身份运行」。
echo   ★ 以后升级：把新版本解压覆盖到同一个目录即可，这个快捷方式不用重新创建。
echo.
pause
