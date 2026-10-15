@echo off
setlocal EnableExtensions
title IPRC1000 遥控器适配器

rem ============================================================================
rem  IPRC1000 遥控器适配器 —— 推荐入口（桌面快捷方式指向本文件）
rem
rem  为什么需要它：
rem    主程序是「框架依赖」发布 —— 安装包只有约 4MB，不打包 140MB 的 .NET 运行时，
rem    而是共用系统已装的那一份（微软推荐做法）。代价是机器上必须有它。
rem    本文件负责先确保运行时到位，再启动主程序。
rem    直接双击 exe，在缺 .NET 的机器上只会弹一个英文的「需要下载 .NET」对话框。
rem
rem  设计：本文件**只做薄封装**，检测与安装全在
rem    tools\准备运行时.ps1
rem  里实现 —— 这样「一键部署.ps1」可以复用同一份逻辑，不会出现两份实现漂移。
rem    一切正常时该脚本静默退出（-Quiet），本文件只留下一瞬间的窗口。
rem
rem  用法：双击本文件即可（也可以右键「以管理员身份运行」）。
rem
rem  ★ 本文件必须保存为 **GBK / ANSI** 编码 —— cmd 按控制台代码页(cp936)逐字节解析，
rem    UTF-8 的中文会被拆错字节，可能连命令本身都解析坏。
rem    tools\package.py 会校验本文件是 GBK 而不是 UTF-8。
rem ============================================================================

set "HERE=%~dp0"
rem 本文件放在程序目录里、或它的 tools 子目录里都能用
set "APPDIR=%HERE%"
if not exist "%APPDIR%IPRC1000 Adapter.exe" (
  if exist "%HERE%..\IPRC1000 Adapter.exe" set "APPDIR=%HERE%..\"
)
if not exist "%APPDIR%IPRC1000 Adapter.exe" (
  echo.
  echo   没找到 IPRC1000 Adapter.exe
  echo   请把本文件放在程序目录里（或它的 tools 子目录）再运行。
  echo.
  pause
  exit /b 1
)

rem ── ① 确保 .NET 桌面运行时（检测 + 缺则自动安装）────────────────────────────
rem    -Quiet：已就绪时不输出，保持启动干净
set "PREP=%APPDIR%tools\准备运行时.ps1"
if exist "%PREP%" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%PREP%" -Quiet -AppDir "%APPDIR%"
  if errorlevel 1 (
    echo.
    echo   [XX] .NET 桌面运行时没准备好，主程序无法启动。
    echo        上面的信息说明了原因。装好后重新双击本文件即可。
    echo.
    echo        也可以手动安装后再试：
    echo          https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0
    echo.
    pause
    exit /b 1
  )
) else (
  rem 没有准备脚本就直接启动 —— 至少让主程序自己给出提示，比什么都不做强
  echo   [!!] 缺少 tools\准备运行时.ps1，跳过运行时检查直接启动。
)

rem ── ② 启动主程序 ──────────────────────────────────────────────────────────
rem    主程序自己的「环境自检」会接着处理虚拟声卡 / 驱动 / 麦克风等
start "" "%APPDIR%IPRC1000 Adapter.exe"
exit /b 0
