> ⚠ 历史快照（2026-11-06）：本文关于「改绑 HID 会导致接口消失/按键全断」「按键只走 Raw Input」「重新配对是唯一解」的结论已于 2026-11-07 更正。当前口径见 VERSION.md 与 IPRC1000_Native_用户手册.html：改绑可选（HID_Inst 接口照常注册）；按键主路径 ETW BthPort ACL；休眠后全断的真因是 Realtek 组合卡蓝牙链路断，处理是禁用其蓝牙换独立适配器。

﻿# 链路验收（只读，5 秒出结论）
# 对应《测试协议》B1-B4 四条判据，客户机/开发机通用。
#   powershell -NoProfile -ExecutionPolicy Bypass -File check-link.ps1
#   可选：-Mac 20c047f313d6

param([string]$Mac = '20c047f313d6')

$ErrorActionPreference = 'SilentlyContinue'
function P([string]$s) { Write-Host $s }

$k = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
$v = Get-ItemProperty $k
if (-not $v) { P "X  配对记录不存在：$Mac（这台机器没配过这只遥控器）"; exit 1 }

$ok = 0

# ---- [1] 到底连没连本机 ----
# ⚠ BTHPORT 的 LastConnected 虽然声明是 FILETIME，但**存的是本地时刻**。
#   用 UtcNow 去比会差 8 小时、算成负数（本机实测）。最稳的做法是拿 Ticks 直接相减。
$lc = [datetime]::FromFileTimeUtc([int64]$v.LastConnected)
$age = [math]::Round(((Get-Date).Ticks - $lc.Ticks) / 10000000)
$lcText = $lc.ToString('yyyy-MM-dd HH:mm:ss')
P ("[1] LastConnected = {0}   (距今 {1:N0} 秒 / {2:N1} 分钟)" -f $lcText, $age, ($age / 60))
if ($age -lt 300) { P "    OK"; $ok++ } else { P "    X  很久没连 —— 遥控器连的是别的主机，或没唤醒" }

# ---- [2] 三个服务节点 ----
P "[2] BTHENUM 服务节点："
$svcs = @()
foreach ($kk in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM' | Where-Object { $_.PSChildName -match '00001124|00001000|00001200' })) {
    foreach ($inst in (Get-ChildItem $kk.PSPath)) {
        if ($inst.PSChildName -notmatch $Mac) { continue }
        $p2 = Get-ItemProperty $inst.PSPath
        $short = ($kk.PSChildName -split '_')[0]
        P ("    {0}  Service=[{1}]" -f $short, $p2.Service)
        $svcs += [string]$p2.Service
    }
}
if ($svcs -contains 'HidBth') { P "    OK  有 Service=HidBth"; $ok++ } else { P "    X   没有 Service=HidBth 的 {00001124} 节点" }

# ---- [3] SDP 缓存 ----
P "[3] SDP 缓存（ServicesFor*）："
$recs = 0
foreach ($s in (Get-ChildItem $k | Where-Object { $_.PSChildName -like 'ServicesFor*' })) {
    $n = @(Get-ChildItem $s.PSPath).Count
    $recs += $n
    P ("    {0} = {1} 条" -f $s.PSChildName, $n)
}
if ($recs -gt 0) { P "    OK  $recs 条"; $ok++ } else { P "    X   0 条 —— SDP 从未成功（孤儿态，见协议 §9.3）" }

# ---- [4] PnP 实例：BTHENUM 的 HID 服务节点 + HID 子节点 ----
# 两者必须分开看：
#   · 有 {00001124} 注册表键 ≠ 有 PnP 实例（键只表示"设备声明支持 HID"）
#   · 有 BTHENUM 实例 ≠ 长出 HID 子节点（后者要 HidBth 真正绑定）
P "[4] PnP 实例："
$hasHidSvc = $false
$hasKbd = $false
$any = $false
$pk = @(Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '00001124' -and $_.InstanceId -match '00020A5C' })
foreach ($d in $pk) {
    $any = $true
    $s = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue).Data
    $isHid = $false
    if ($d.InstanceId -match '^HID\\') { $isHid = $true }
    $tag = 'BTHENUM  '
    if ($isHid) { $tag = 'HID子节点' }
    $short = ($d.InstanceId -replace '\\[78]&.*', '')
    P ("    {0} status={1,-8} class={2,-10} svc=[{3}]  {4}" -f $tag, $d.Status, $d.Class, $s, $short)
    if ((-not $isHid) -and ($s -eq 'HidBth')) { $hasHidSvc = $true }
    if ($isHid -and ($s -eq 'kbdhid')) { $hasKbd = $true }
}
if (-not $any) { P "    X   一个 PnP 实例都没有 —— BthPort 没为 HID 服务建设备节点" }
if (-not $hasHidSvc) { P "    X   没有 Service=HidBth 的 BTHENUM 实例 —— HidBth 没加载" }
if (-not $hasKbd) { P "    X   没有 svc=kbdhid 的 HID 子节点 —— 按键收不到" }
if ($hasHidSvc -and $hasKbd) { $ok++ }

P ""
if ($ok -ge 4) {
    P "==== 结论：链路 OK ==== 可以启动程序，按遥控器看 keyEvents 是否增长"
} else {
    P ("==== 结论：未恢复（{0}/4）==== 按上面标 X 的那条处理，见《测试协议》§9.3" -f $ok)
}
