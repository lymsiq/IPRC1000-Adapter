# 软件补齐 {00001124} HID 服务记录（不换硬件、不改设备绑定）
#
# 原理：
#   Windows 对每个已配对设备，在 BTHPORT\Parameters\Devices\<mac>\
#   ServicesFor<本地适配器MAC>\ 下按服务 GUID 存一个"该设备支持此服务"的标记。
#   {00001000} 服务发现 / {00001200} 设备标识 / {00001124} HID。
#   客户机配对后只出现了前两个，缺 HID —— 于是 HidBth 不加载、PSM 0x0011
#   没有监听者、遥控器发来的 ACL 数据全是信令、没有一条 HID 报告。
#
#   这个键的内容极简（实测开发机）：
#       PriLangServiceName = "Broadcom Bluetooth Wireless  Remote Control"（ASCII 填充 248 字节）
#       DeviceString       = ""
#       CounterInstanceId  = 0
#       Enabled            = 1        ← 关键是这个
#
# 本脚本只做一件事：缺哪个适配器桶里的 {00001124} 就补哪个，然后重启适配器。
# 不改驱动绑定、不动 Enum 节点、不碰配对密钥。
# 回退：删掉补出来的 {00001124} 子键即可。

param(
    [string]$Mac = '20c047f313d6',
    [switch]$Apply
)

$ErrorActionPreference = 'SilentlyContinue'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在请求提升…"
    $a = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mac $Mac"
    if ($Apply) { $a += " -Apply" }
    Start-Process powershell.exe -ArgumentList $a -Verb RunAs
    exit
}

$logDir = 'D:\IPRC1000-Native\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force $logDir | Out-Null }
$log = Join-Path $logDir 'inject-hid.log'
"===== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') =====" | Set-Content -Path $log -Encoding UTF8
function L([string]$s) {
    Write-Host $s
    Add-Content -Path $log -Value $s -Encoding UTF8
}

L ("管理员=" + $isAdmin + "  模式=" + $(if ($Apply) { 'APPLY' } else { '只读' }) + "  MAC=" + $Mac)

$base = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
if (-not (Test-Path $base)) {
    L "X 配对记录不存在：$Mac —— 先配对，再跑本脚本"
    Read-Host "按回车关闭"
    exit 1
}

$sfKeys = @(Get-ChildItem $base | Where-Object { $_.PSChildName -like 'ServicesFor*' })
if ($sfKeys.Count -eq 0) {
    L "X 没有 ServicesFor* 子键 —— SDP 完全没跑过，本脚本无能为力，必须先重新配对"
    Read-Host "按回车关闭"
    exit 1
}
L ("发现 " + $sfKeys.Count + " 个适配器桶")

$guid = '{00001124-0000-1000-8000-00805f9b34fb}'
$need = 0
$fixed = 0

foreach ($s in $sfKeys) {
    L ""
    L ("桶: " + $s.PSChildName)
    $kids = @(Get-ChildItem $s.PSPath | ForEach-Object { $_.PSChildName })
    foreach ($k in $kids) { L ("    已有: " + $k) }

    $target = Join-Path $s.PSPath $guid
    if (Test-Path -LiteralPath $target) {
        $en = (Get-ItemProperty -LiteralPath (Join-Path $target 'C00000000')).Enabled
        L ("    {00001124} 已存在，Enabled=" + $en)
        continue
    }

    $need++
    L "    {00001124} 缺失"
    if (-not $Apply) { continue }

    $child = Join-Path $target 'C00000000'
    New-Item -Path $child -Force | Out-Null
    if (-not (Test-Path -LiteralPath $child)) {
        L "    X 创建注册表键失败"
        continue
    }
    $bytes = New-Object byte[] 248
    $ascii = [Text.Encoding]::ASCII.GetBytes('Broadcom Bluetooth Wireless  Remote Control')
    [Array]::Copy($ascii, $bytes, [Math]::Min($ascii.Length, 248))
    Set-ItemProperty -LiteralPath $child -Name 'PriLangServiceName' -Value $bytes -Type Binary
    Set-ItemProperty -LiteralPath $child -Name 'DeviceString' -Value '' -Type String
    Set-ItemProperty -LiteralPath $child -Name 'CounterInstanceId' -Value 0 -Type DWord
    Set-ItemProperty -LiteralPath $child -Name 'Enabled' -Value 1 -Type DWord

    $chk = Get-ItemProperty -LiteralPath $child
    L ("    已写入 Enabled=" + $chk.Enabled + " nameLen=" + $chk.PriLangServiceName.Length)
    $fixed++
}

L ""
if ($need -eq 0) {
    L "结论：所有适配器桶都已有 {00001124}，本次无需注入。"
    L "      若按键仍无反应，说明键在但 HidBth 没加载 —— 重启一次电脑再看。"
} elseif ($Apply) {
    L ("结论：已补 " + $fixed + " 处。下面重启适配器让 BthPort 重新读取…")
    $adapters = @(Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like 'USB*' })
    foreach ($d in $adapters) { L ("  " + $d.Status + " | " + $d.FriendlyName + " | " + $d.InstanceId) }
    foreach ($d in $adapters) { Disable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false }
    Start-Sleep -Seconds 4
    foreach ($d in $adapters) { Enable-PnpDevice -InstanceId $d.InstanceId -Confirm:$false }
    Start-Sleep -Seconds 4
    foreach ($d in @(Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like 'USB*' })) {
        L ("  重启后: " + $d.FriendlyName + " = " + $d.Status)
    }
    L ""
    L "下一步："
    L "  1. 按一下遥控器唤醒（或先断开再连一次）"
    L "  2. 跑 check-link 看 [4] 是否变成 svc=kbdhid"
    L "  3. 若还没出现，重启一次电脑（BthPort 只在启动时完整重建服务表）"
} else {
    L ("结论：有 " + $need + " 处缺失。加 -Apply 执行注入。")
}

Read-Host "按回车关闭"
