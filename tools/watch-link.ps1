# 实时链路监控（跑 60 秒，这期间请按遥控器）
#   powershell -NoProfile -ExecutionPolicy Bypass -File watch-link.ps1
# 每 2 秒打一行，看三个值是否变化：
#   LastConnected  —— 变了说明"刚刚连上过"（物理链路 OK）
#   HID 子节点状态 —— Unknown→OK 说明 HidBth 把它拉起来了
#   keyEvents      —— 涨了说明按键真的到了程序

param(
    [int]$Seconds = 60,
    [string]$Mac = '20c047f313d6'
)

$ErrorActionPreference = 'SilentlyContinue'
$base = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
$statsFile = 'D:\IPRC1000-Native\logs\bridge-stats.json'
$out = 'D:\IPRC1000-Native\logs\watch-link.txt'

"===== 监控开始 $(Get-Date -Format 'HH:mm:ss')  时长 ${Seconds}s =====" | Set-Content -Path $out -Encoding UTF8
Write-Host "监控中… 现在请按遥控器（每隔 2 秒刷新一行）" -ForegroundColor Cyan
Write-Host ""

$rounds = [int]($Seconds / 2)
$prevLc = ''
$prevKey = -1

for ($i = 0; $i -lt $rounds; $i++) {
    $now = Get-Date
    $v = Get-ItemProperty $base
    $lcText = 'n/a'
    $ageText = 'n/a'
    if ($v -and $v.LastConnected) {
        $lc = [datetime]::FromFileTimeUtc([int64]$v.LastConnected)
        $age = [math]::Round((($now).Ticks - $lc.Ticks) / 10000000)
        $lcText = $lc.ToString('HH:mm:ss')
        $ageText = "$age s 前"
    }

    $hidStatus = 'none'
    foreach ($d in @(Get-PnpDevice | Where-Object { $_.InstanceId -match '^HID\\' -and $_.InstanceId -match '00001124' -and $_.InstanceId -match '00020A5C' })) {
        $hidStatus = $d.Status
    }

    $keyEvents = -1
    $hidMiss = -1
    if (Test-Path $statsFile) {
        $j = Get-Content $statsFile -Raw | ConvertFrom-Json
        $keyEvents = $j.keyEvents
        $hidMiss = $j.hidMiss
    }

    $mark = ''
    if ($lcText -ne $prevLc -and $prevLc -ne '') { $mark = '  <== LastConnected 刷新了' }
    if ($prevKey -ge 0 -and $keyEvents -gt $prevKey) { $mark += '  <== 按键到达' }

    $line = ("{0:HH:mm:ss}  LastConnected={1} ({2})  HID={3,-8} keyEvents={4}  hidMiss={5}{6}" -f $now, $lcText, $ageText, $hidStatus, $keyEvents, $hidMiss, $mark)
    Write-Host $line
    Add-Content -Path $out -Value $line -Encoding UTF8

    $prevLc = $lcText
    $prevKey = $keyEvents
    Start-Sleep -Seconds 2
}

"===== 监控结束 $(Get-Date -Format 'HH:mm:ss') =====" | Add-Content -Path $out -Encoding UTF8
Write-Host ""
Write-Host "结果已写入 $out" -ForegroundColor Green
Read-Host "按回车关闭"
