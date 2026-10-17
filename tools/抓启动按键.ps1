# 抓「启动瞬间的按键事件」
#
# 用途：程序里出现过"刚启动就莫名触发一次某个按键"的怪事。
# 日志默认是关的（发布版性能优先），这个脚本只临时打开一次日志，
# 起一次程序、等几秒、把新增的按键日志打出来 —— 不改任何配置。
#
# 用法：右键"使用 PowerShell 运行"，或在终端里
#       powershell -ExecutionPolicy Bypass -File tools\抓启动按键.ps1
#
# 读法（三种情况对应三处不同的责任）：
#   KEY + APPLY + INJECT   事件来自遥控器/桥（链路），与我们代码无关
#   APPLY 或 INJECT 但无 KEY  我们内部误触发
#   只有 INJECT            注入侧

param([int]$Seconds = 8)

$ErrorActionPreference = 'Stop'
$app  = Join-Path (Split-Path $PSScriptRoot -Parent) 'IPRC1000_Adapter\IPRC1000 Adapter.exe'
$log  = Join-Path $env:APPDATA 'IPRC1000 Adapter\key-events.log'

if (-not (Test-Path $app)) { Write-Host "找不到 $app" -ForegroundColor Red; exit 1 }

$mark = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
Write-Host "1) 临时打开日志并启动程序（现有日志不会被清空）..." -ForegroundColor Cyan
$env:IPRC1000_LOG = 'on'
try {
    Start-Process $app
    Write-Host "2) 等 $Seconds 秒（这段时间里请观察屏幕上到底弹了什么）..." -ForegroundColor Cyan
    Start-Sleep -Seconds $Seconds
}
finally {
    Get-Process 'IPRC1000 Adapter' -ErrorAction SilentlyContinue | Stop-Process -Force
    Get-Process 'etw_consumer'     -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-Item Env:\IPRC1000_LOG -ErrorAction SilentlyContinue
}

Write-Host "3) 本次启动新增的日志行：" -ForegroundColor Cyan
if (-not (Test-Path $log)) { Write-Host "（日志文件不存在 —— 说明连日志都没写出来）" -ForegroundColor Yellow; exit }

$fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
try {
    $fs.Seek([Math]::Min($mark, $fs.Length), 'Begin') | Out-Null
    $txt = (New-Object IO.StreamReader($fs)).ReadToEnd()
}
finally { $fs.Close() }

if ([string]::IsNullOrWhiteSpace($txt)) {
    Write-Host "（本次启动一条日志都没有）" -ForegroundColor Yellow
} else {
    $txt
}

Write-Host ""
Write-Host "把上面这些行发回来就行。重点看第一行是 KEY（来自遥控器）还是 APPLY/INJECT（来自本程序）。" -ForegroundColor Green
