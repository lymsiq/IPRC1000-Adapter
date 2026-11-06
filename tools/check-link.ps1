# 链路验收（只读，5 秒出结论）
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

# ---- [4] HID 子节点绑的是谁 ----
P "[4] HID 子节点："
$hasKbd = $false
$any = $false
# 只取 HID 子节点（HID\{00001124…}_VID&00020a5c…），
# 不能笼统 -match '00020A5C' —— BTHENUM 的 {00001000}/{00001200} 节点也带这个串，会被误算。
foreach ($d in (Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -match '^HID\\' -and $_.InstanceId -match '00001124' })) {
    $any = $true
    $s = (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_Service -ErrorAction SilentlyContinue).Data
    $short = ($d.InstanceId -replace '\\[78]&.*', '')
    P ("    {0,-8} svc=[{1}]  {2}" -f $d.Status, $s, $short)
    if ($s -eq 'kbdhid') { $hasKbd = $true }
}
if (-not $any) { P "    X   一个实例都没有 —— 节点被删了，要重连才会重新长出来" }
elseif ($hasKbd) { P "    OK  svc=kbdhid，按键能读到（会打字符，属已知边界）"; $ok++ }
else { P "    X   有实例但 svc 不是 kbdhid —— 按键收不到" }

P ""
if ($ok -ge 4) {
    P "==== 结论：链路 OK ==== 可以启动程序，按遥控器看 keyEvents 是否增长"
} else {
    P ("==== 结论：未恢复（{0}/4）==== 按上面标 X 的那条处理，见《测试协议》§9.3" -f $ok)
}
