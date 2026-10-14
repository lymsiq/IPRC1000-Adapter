@echo off
rem 加载 MSVC x64 编译环境后执行传入的命令
rem 用法: msvc-env.cmd <命令> [参数...]
rem 例:   msvc-env.cmd cargo build --manifest-path F:\vibe\Voice_VibeCoding\src-tauri\Cargo.toml
rem       msvc-env.cmd npm run tauri:dev
setlocal
call "D:\BuildTools\VS2022\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
if errorlevel 1 (
  echo [msvc-env] 加载 vcvars64.bat 失败
  exit /b 1
)
%*
endlocal
