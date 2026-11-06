# IPRC1000 配对修复（客户机专用，会改注册表 —— 已自动备份）
#
# 做三件事：
#   1) 备份并删除这只遥控器的全部残留（BTHPORT 配对记录 / BTHENUM 服务节点 / HID 子节点）
#      —— 只删「设备」不清这些，下次配对被当成"重连"，Windows 会跳过 SDP
#   2) 重启蓝牙适配器，强制重新枚举
#   3) 告诉你下一步怎么配对（配对全程必须一直按遥控器键，SDP 只在那几秒跑）
#
# 用法：右键「以管理员身份运行」，或直接双击（会自动请求提权）
#   可选：-Mac 20c047f313d6

param([string]$Mac = '20c047f313d6')

$ErrorActionPreference = 'SilentlyContinue'

# ---------- 自动提权 ----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "需要管理员权限，正在请求提升…"
    $args = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Mac $Mac"
    Start-Process powershell.exe -ArgumentList $args -Verb RunAs
    exit
}

$logDir = 'D:\IPRC1000-Native\logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Force $logDir | Out-Null }
$log = Join-Path $logDir 'fix-pairing.log'

function L([string]$s) {
    Write-Host $s
    Add-Content -Path $log -Value $s -Encoding UTF8
}

"===== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') 修复开始 =====" | Set-Content -Path $log -Encoding UTF8
L ("管理员=" + $isAdmin + "  MAC=" + $Mac)
L ""

# ---------- 1. BTHPORT 配对记录 ----------
$btDev = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
if (Test-Path $btDev) {
    $backup = Join-Path $env:TEMP ("bt-backup-" + $Mac + "-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".reg")
    reg export "HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac" $backup /y | Out-Null
    L "备份 -> $backup"
    $sf = @(Get-ChildItem $btDev | Where-Object { $_.PSChildName -like 'ServicesFor*' })
    L ("原有 ServicesFor 子键 " + $sf.Count + " 个（0 = SDP 从未成功）")
    Remove-Item -Path $btDev -Recurse -Force
    L "已删除 BTHPORT 配对记录"
} else {
    L "BTHPORT 配对记录不存在，跳过"
}

# ---------- 2. BTHENUM 节点（含 Dev_ 父节点与各服务节点） ----------
$macU = $Mac.ToUpper()
$btEnum = 0
foreach ($k in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM')) {
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        if ($inst.PSChildName -match "(?i)$macU") {
            $path = 'HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\' + $k.PSChildName + '\' + $inst.PSChildName
            reg delete $path /f | Out-Null
            $btEnum++
            L ("  删除 BTHENUM: " + $k.PSChildName)
        }
    }
}
L ("BTHENUM 节点删除 " + $btEnum + " 个")

# ---------- 3. HID 子节点 ----------
$hidDel = 0
foreach ($k in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID' | Where-Object { $_.PSChildName -match '(?i)VID[&_](0002)?0A5C' })) {
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        $path = 'HKLM\SYSTEM\CurrentControlSet\Enum\HID\' + $k.PSChildName + '\' + $inst.PSChildName
        reg delete $path /f | Out-Null
        $hidDel++
    }
}
L ("HID 子节点删除 " + $hidDel + " 个")

# ---------- 4. 重启蓝牙适配器 ----------
L ""
L "重启蓝牙适配器…"
L "本机 Bluetooth 类设备（排查用）："
foreach ($d in @(Get-PnpDevice -Class Bluetooth)) {
    L ("  " + $d.Status + " | " + $d.FriendlyName + " | " + $d.InstanceId)
}
# ⚠ 用 -like 'USB*'，不要写 -match '^USB\' —— 正则尾部反斜杠是转义符，
#   整个模式永远匹配不上，适配器重启会被静默跳过（本次实测踩到）。
$adapters = @(Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like 'USB*' })
if ($adapters.Count -eq 0) {
    L "没找到可用的 USB 蓝牙适配器，跳过重启（请手动在设备管理器禁用/启用）"
} else {
    foreach ($a in $adapters) {
        L ("  禁用: " + $a.FriendlyName)
        Disable-PnpDevice -InstanceId $a.InstanceId -Confirm:$false
    }
    Start-Sleep -Seconds 4
    foreach ($a in $adapters) {
        L ("  启用: " + $a.FriendlyName)
        Enable-PnpDevice -InstanceId $a.InstanceId -Confirm:$false
    }
    Start-Sleep -Seconds 3
    foreach ($a in @(Get-PnpDevice -Class Bluetooth | Where-Object { $_.InstanceId -like 'USB*' })) {
        L ("  状态: " + $a.FriendlyName + " = " + $a.Status)
    }
}

# ---------- 5. 下一步 ----------
L ""
L "===== 清理完成，请按顺序操作 ====="
L "1. 遥控器抠电池 10 秒再装回（清掉固件里 PENDING 的 HID 通道）"
L "2. 同时按「上一频道」+「下方向键」→ 回到可配对状态"
L "3. 长按右上角 PWR，直到方形指示灯蓝灯快闪"
L "4. 本机：设置 → 蓝牙和其他设备 → 添加设备 → 蓝牙 → 选 IPRC1000"
L "5. ★ 配对全过程持续按遥控器任意键（SDP 只在配对那几秒跑，一停就超时）"
L "6. 配对完成后等 5 秒，跑 check-link 验证"
L ""
L "日志：$log"

Write-Host ""
Write-Host "完成。日志：$log"
Read-Host "按回车关闭"
