> ⚠ 历史快照（2026-11-06）：本文关于「改绑 HID 会导致接口消失/按键全断」「按键只走 Raw Input」「重新配对是唯一解」的结论已于 2026-11-07 更正。当前口径见 VERSION.md 与 IPRC1000_Native_用户手册.html：改绑可选（HID_Inst 接口照常注册）；按键主路径 ETW BthPort ACL；休眠后全断的真因是 Realtek 组合卡蓝牙链路断，处理是禁用其蓝牙换独立适配器。

﻿#requires -Version 5.1
<#
  diag-bt.ps1  —  IPRC1000 蓝牙按键链路诊断（开发机 / 客户机 通用）
  用法: powershell -NoProfile -ExecutionPolicy Bypass -File diag-bt.ps1
  输出: 控制台 + diag-bt-<hostname>-<yyyyMMdd-HHmmss>.txt

  设计原则：
   1) 只读，不改任何状态
   2) 开发机与客户机跑同一份脚本，输出同一格式，可直接 diff
   3) 7 个检查点，首个 FAIL 即根因层，并给出对应修法
#>

$ErrorActionPreference = 'SilentlyContinue'
$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outFile = Join-Path (Get-Location) ("diag-bt-{0}-{1}.txt" -f $env:COMPUTERNAME, $stamp)
$script:Lines = @()

function W([string]$s = '') {
    Write-Host $s
    $script:Lines += $s
}

function Hdr([string]$t) {
    W ''
    W ('=' * 78)
    W $t
    W ('=' * 78)
}

$fails = @()

Hdr "IPRC1000 蓝牙按键链路诊断"
W ("主机      : {0}" -f $env:COMPUTERNAME)
W ("时间      : {0}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
W ("账号      : {0}" -f $env:USERNAME)
W ("管理员    : {0}" -f ([bool](([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))))
W ("OS        : {0}" -f (Get-CimInstance Win32_OperatingSystem).Caption)
W ("Build     : {0}" -f (Get-CimInstance Win32_OperatingSystem).BuildNumber)

# ---------------------------------------------------------------- C1 适配器
Hdr "[C1] 蓝牙适配器"

# 适配器一定是 USB\ 或 PCI\ 下的物理设备；BTHENUM\ 下的是"设备"不是"适配器"
$btDev = Get-PnpDevice -Class Bluetooth -ErrorAction SilentlyContinue |
         Where-Object { $_.InstanceId -match '^(USB|PCI)\\VID_' }

if (-not $btDev) {
    W '  !! 没找到蓝牙适配器'
    $fails += 'C1'
} else {
    foreach ($d in $btDev) {
        W ("  设备    : {0}" -f $d.FriendlyName)
        W ("  状态    : {0}" -f $d.Status)
        W ("  实例 ID : {0}" -f $d.InstanceId)
        if ($d.Status -ne 'OK') { $fails += 'C1' }
        # 驱动 INF
        $drv = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverInfPath' -ErrorAction SilentlyContinue
        $prv = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverProvider' -ErrorAction SilentlyContinue
        $ver = Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName 'DEVPKEY_Device_DriverVersion' -ErrorAction SilentlyContinue
        W ("  驱动 INF: {0}" -f $drv.Data)
        W ("  驱动厂商: {0}" -f $prv.Data)
        W ("  驱动版本: {0}" -f $ver.Data)
    }
    # 厂商私有 HCI 通道（Realtek 过滤驱动是否挂上）
    $rtk = Get-Service RtkBtManServ -ErrorAction SilentlyContinue
    if ($rtk) { W ("  RtkBtManServ: {0}" -f $rtk.Status) }
}

# ---------------------------------------------------------------- C2 BTHENUM 节点
Hdr "[C2] BTHENUM 服务节点（★ 最关键：HID 服务节点是否存在）"

$btn = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM' -ErrorAction SilentlyContinue
if (-not $btn) {
    W '  (Enum\BTHENUM 为空)'
} else {
    foreach ($k in $btn) {
        $svc = (Get-ItemProperty $k.PSPath -Name 'Service' -ErrorAction SilentlyContinue).Service
        $mark = ''
        if ($k.PSChildName -match '1124') { $mark = '   <== HID 服务节点' }
        W ("  {0}" -f $k.PSChildName)
        W ("      Service = {0}{1}" -f $(if ($svc) { $svc } else { '(空)' }), $mark)
    }
}

$hidNode = $btn | Where-Object { $_.PSChildName -match '1124' }
if (-not $hidNode) {
    W '  !! 没有 {00001124-...} HID 服务节点  =>  HidBth 不会绑定'
    $fails += 'C2'
} else {
    $svc = (Get-ItemProperty $hidNode[0].PSPath -Name 'Service' -ErrorAction SilentlyContinue).Service
    if ($svc -eq 'HidBth') { W '  [OK] Service = HidBth 已绑定' }
    else { W ("  !! Service = '{0}'，不是 HidBth" -f $svc); $fails += 'C2' }
}

# ---------------------------------------------------------------- C3 配对记录
Hdr "[C3] 蓝牙配对记录"

$devRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices'
$devs = Get-ChildItem $devRoot -ErrorAction SilentlyContinue
if (-not $devs) {
    W '  (没有任何配对记录)'
    $fails += 'C3'
} else {
    foreach ($d in $devs) {
        $p = Get-ItemProperty $d.PSPath
        $nm = ''
        if ($p.Name -is [byte[]]) { $nm = -join ($p.Name | Where-Object { $_ -ne 0 } | ForEach-Object { [char]$_ }) }
        $cod = $p.COD
        $codHex = ''
        if ($cod) { $codHex = ('0x{0:X6}' -f [int64]$cod) }
        $minor = ''
        if ($cod) { $minor = 'major=' + ((($cod -shr 8) -band 0x1F)) + ' minor=' + ((($cod -shr 2) -band 0x3F)) }
        W ("  {0}  Name='{1}'  COD={2} ({3})" -f $d.PSChildName, $nm, $codHex, $minor)
        # 缓存 SDP 服务记录
        $sub = Get-ChildItem $d.PSPath -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like 'ServicesFor*' }
        if ($sub) {
            $sp = Get-ItemProperty $sub[0].PSPath
            $meta = @('SSP Paired','SSP MITM Protected','SSP Supported','AuthenticationRequirements',
                      'RemoteAuthenticationRequirements','IoCapability','BasebandSupport','BRFlags',
                      'BRExtendedDeviceInfoFlags')
            $svcRecs = @()
            foreach ($prop in $sp.PSObject.Properties) {
                if ($prop.Name -like 'PS*') { continue }
                if ($meta -contains $prop.Name) { continue }
                $svcRecs += $prop.Name
            }
            W ("      缓存 SDP 服务记录: {0} 条" -f $svcRecs.Count)
            if ($svcRecs.Count -gt 0) {
                foreach ($s in $svcRecs) { W ("        - {0}" -f $s) }
            } else {
                W '      !! 0 条 => SDP 从未成功执行过（没有服务发现，就不可能有 HID 节点）'
                $fails += 'C3'
            }
        } else {
            W '      !! 没有 ServicesFor 子键 => SDP 从未执行'
            $fails += 'C3'
        }
    }
}

# ---------------------------------------------------------------- C4 服务
Hdr "[C4] 相关系统服务"
foreach ($n in @('BthServ','hidserv','BthAvctpSvc','BTAGService')) {
    $s = Get-Service -Name $n -ErrorAction SilentlyContinue
    if ($s) {
        W ("  {0,-14} {1,-9} 启动={2}" -f $s.Name, $s.Status, $s.StartType)
        if ($s.Status -ne 'Running' -and $n -in @('BthServ','hidserv')) { $fails += "C4:$n" }
    } else {
        W ("  {0,-14} (不存在)" -f $n)
    }
}

# ---------------------------------------------------------------- C5 Enum\HID 残留
Hdr "[C5] Enum\HID 残留节点（防假绿灯：这里读到的'键盘'可能已经不是当前设备）"
$hidRes = Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID' -ErrorAction SilentlyContinue |
          Where-Object { $_.PSChildName -match 'A5C|8502|BTHENUM' }
if ($hidRes) {
    foreach ($r in $hidRes) { W ("  残留: {0}" -f $r.PSChildName) }
    W '  说明: 这些是历史节点，不代表当前 HidBth 已绑定 -> 判活必须看 C2，不能看这里'
} else {
    W '  无残留（干净）'
}

# ---------------------------------------------------------------- C6 键盘设备
Hdr "[C6] 当前 HID 键盘类设备"
Get-PnpDevice -Class Keyboard -ErrorAction SilentlyContinue | ForEach-Object {
    W ("  {0,-10} {1,-34} {2}" -f $_.Status, $_.FriendlyName, $_.InstanceId)
}

# ---------------------------------------------------------------- C7 最近事件
Hdr "[C7] 最近 30 分钟蓝牙相关系统事件"
$ev = Get-WinEvent -LogName System -MaxEvents 500 -ErrorAction SilentlyContinue |
      Where-Object { $_.ProviderName -match 'BTH|Bluetooth|HidBth' -and $_.TimeCreated -gt (Get-Date).AddMinutes(-30) }
if ($ev) {
    $ev | Select-Object -First 25 | ForEach-Object {
        W ("  {0:HH:mm:ss} {1,-10} {2,-4} {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -split "`n")[0]))
    }
    W ("  ... 共 {0} 条" -f $ev.Count)
} else {
    W '  (无)'
}

# ---------------------------------------------------------------- 结论
Hdr "结论"

if ($fails.Count -eq 0) {
    W '  [OK] 7 个检查点全部通过 —— 链路层正常，若仍无按键请查客户端 Raw Input'
} else {
    W ("  失败检查点: {0}" -f ((($fails | Select-Object -Unique) -join ', ')))
    W ''
    if ($fails -contains 'C2' -or $fails -contains 'C3') {
        W '  ★ 根因层判定：SDP 服务发现缺失 -> 无 HID 服务节点 -> HidBth 未绑定'
        W '    -> 主机 L2CAP 无 PSM 0x0011 监听 -> 遥控器 ConnReq 必然 REFUSED_PSM'
        W '    修法：删除该设备配对 -> 遥控器进配对模式 -> 重新配对（强制重跑 SDP）'
        W '    注意：--restore-keyboard 无效，因为没有节点可"恢复"，必须重新配对'
    }
    if ($fails -contains 'C1') { W '    修法：检查适配器状态 / 重新安装厂商驱动' }
    if ($fails -match 'C4') { W '    修法：Start-Service BthServ / hidserv' }
}

W ''
W ("报告已保存: {0}" -f $outFile)
$script:Lines | Out-File -FilePath $outFile -Encoding UTF8
