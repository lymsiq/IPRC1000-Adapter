@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo ============================================
echo   IPRC1000 Adapter  -  .NET 8 + WebView2
echo ============================================
echo.

echo [1/2] 发布（框架依赖 + 单文件）...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo [X] 发布失败
  exit /b 1
)

del /q publish\*.xml 2>nul

echo.
echo [2/2] 完成
echo.
for %%F in ("publish\IPRC1000 Adapter.exe") do echo   主程序: %%~fF  (%%~zF 字节)
echo   资源目录: publish\ui  publish\assets
echo.
echo   运行: publish\IPRC1000 Adapter.exe
echo   自检: publish\IPRC1000 Adapter.exe --selftest report.json
echo.
echo 依赖（目标机需已安装）:
echo   - .NET 8 Desktop Runtime
echo   - WebView2 Runtime（Win10/11 通常自带）
echo.
echo 需要完全自包含（不依赖 .NET 运行时，约 70 MB）:
echo   dotnet publish -c Release -r win-x64 --self-contained true ^
echo     -p:PublishSingleFile=true -o publish-standalone
echo.
pause
