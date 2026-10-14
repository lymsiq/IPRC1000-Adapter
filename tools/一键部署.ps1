# ============================================================================
#  IPRC1000 遥控器适配器 —— 一键部署
#
#  做四件事（已完成的会自动跳过）：
#    ① 安装 WinUHid 虚拟键盘驱动（注入「右 Alt 长按说话」必需）
#    ② 把遥控器的 HID 设备驱动从「键盘」切换成「普通 HID 设备」
#       —— 这一步**不需要装任何新驱动**（用的是 Windows 自带 input.inf）
#    ③ 安装 VB-CABLE 虚拟声卡（豆包转写用的音频回环）
#    ④ 启动主程序（它自己会：把默认录音设备设成 CABLE Output、拉起语音桥、自动切驱动）
#
#  用法：
#    右键「使用 PowerShell 运行」，或在管理员 PowerShell 里：
#      Set-ExecutionPolicy -Scope Process Bypass -Force; .\一键部署.ps1
# ============================================================================

[CmdletBinding()]
param([switch]$SkipSoundcard)

$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Info($m) { Write-Host "  $m" }
function Ok($m)   { Write-Host "  [OK] $m"   -ForegroundColor Green }
function Warn($m) { Write-Host "  [!!] $m"   -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [XX] $m"   -ForegroundColor Red }

# ── 0. 管理员权限（不是就自提权重开）────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isAdmin) {
    Warn '需要管理员权限，正在申请提权…'
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    exit
}
Ok '管理员权限已确认'

Write-Host ''
Write-Host '╔══════════════════════════════════════════════════════════╗'
Write-Host '║   IPRC1000 遥控器适配器 · 一键部署                        ║'
Write-Host '╚══════════════════════════════════════════════════════════╝'

# ── 定位目录 ───────────────────────────────────────────────────────────────
$Root = Split-Path -Parent $PSScriptRoot          # 包根目录（脚本在 tools\ 下）
if (-not (Test-Path (Join-Path $Root 'IPRC1000 Adapter.exe'))) { $Root = $PSScriptRoot }
$Exe  = Join-Path $Root 'IPRC1000 Adapter.exe'
if (-not (Test-Path $Exe)) { Fail "找不到主程序，请确认脚本在解压后的包里：$Root"; exit 1 }
Ok "程序目录：$Root"

# ── 1. WinUHid 虚拟键盘驱动 ────────────────────────────────────────────────
Write-Host ''
Write-Host '① WinUHid 虚拟键盘驱动'
$uhidDll = Join-Path $env:WINDIR 'System32\WinUHid.dll'
if (Test-Path $uhidDll) {
    Ok '已安装（System32\WinUHid.dll 存在），跳过'
} else {
    $installer = Join-Path $Root 'assets\winuhid\Run-Install-Force.cmd'
    if (-not (Test-Path $installer)) { $installer = Join-Path $Root 'assets\winuhid\Run-Install.cmd' }
    if (Test-Path $installer) {
        Info "执行：$installer"
        & cmd.exe /c "`"$installer`""
        Start-Sleep -Seconds 3
        if (Test-Path $uhidDll) { Ok '安装完成' } else { Warn '安装脚本已跑完，但没检测到 DLL —— 请看它的输出' }
    } else {
        Warn "没找到安装脚本（$installer），请手动安装 assets\winuhid 下的驱动"
    }
}

# ── 2. 设备驱动绑定：键盘 → 普通 HID 设备 ─────────────────────────────────
Write-Host ''
Write-Host '② 遥控器设备驱动（不需要新驱动，用系统自带的 input.inf）'

function Get-RemoteService {
    $base = 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID'
    Get-ChildItem $base -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match '0a5c' -and $_.PSChildName -match '8502' } |
        ForEach-Object {
            Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
                $svc = (Get-ItemProperty $_.PSPath -Name Service -ErrorAction SilentlyContinue).Service
                if ($svc) { $svc } else { '(无)' }
            }
        }
}

$svc = Get-RemoteService
if (-not $svc) {
    Warn '没检测到遥控器的 HID 节点 —— 请先蓝牙配对遥控器，再重跑本脚本'
} elseif ($svc -notcontains 'kbdhid') {
    Ok "当前绑定：$($svc -join ', ') —— 已经不是键盘，跳过"
} else {
    $setter = Join-Path $Root 'tools\SetHidDriver.exe'
    if (Test-Path $setter) {
        Info '切换成「HID-compliant device」（非键盘 → 系统不再产生 m/i、也没有丢 UP 卡键）…'
        & $setter
        Start-Sleep -Seconds 3
        $svc2 = Get-RemoteService
        if ($svc2 -notcontains 'kbdhid') { Ok "已切换：$($svc2 -join ', ')" }
        else { Fail '切换后仍是 kbdhid —— 请重试，或在设备管理器里手动更换驱动' }
    } else {
        Warn "没找到 $setter —— 可打开主程序，在「环境设置 → 环境自检」里点「还原成键盘驱动 / 切换设备驱动」"
    }
}

# ── 3. VB-CABLE 虚拟声卡 ──────────────────────────────────────────────────
Write-Host ''
Write-Host '③ VB-CABLE 虚拟声卡（豆包转写用的音频回环）'
$cable = Get-PnpDevice -Class MEDIA -ErrorAction SilentlyContinue |
         Where-Object { $_.FriendlyName -match 'CABLE' }
if ($cable) {
    Ok "已安装（$($cable[0].FriendlyName)），跳过"
} elseif ($SkipSoundcard) {
    Warn '按要求跳过'
} else {
    $zip = Join-Path $Root 'assets\VBCABLE_Driver_Pack45.zip'
    if (Test-Path $zip) {
        $tmp = Join-Path $env:TEMP 'iprc1000_vbcable'
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        Expand-Archive -Path $zip -DestinationPath $tmp -Force
        $setup = Get-ChildItem $tmp -Recurse -Filter 'VBCABLE_Setup_x64.exe' |
                 Select-Object -First 1
        if ($setup) {
            Info '运行 VB-CABLE 安装程序（会弹窗，点 Install Driver 即可）…'
            Start-Process $setup.FullName -Verb RunAs -Wait
            Ok '安装程序已结束'
        } else { Warn '压缩包里没找到 VBCABLE_Setup_x64.exe' }
    } else {
        Warn "没找到 $zip —— 可到 VB-Audio 官网下载安装"
    }
}

# ── 4. 启动主程序（它会完成剩下的自动化）──────────────────────────────────
Write-Host ''
Write-Host '④ 启动主程序（剩余步骤由它自动完成）'
Info '它会自动：把默认录音设备设成 CABLE Output → 拉起语音桥 → 把设备驱动切成非键盘 → 应用按键映射'
Start-Process $Exe
Start-Sleep -Seconds 6
Ok '已启动'

Write-Host ''
Write-Host '══════════════════════════════════════════════════════════'
Write-Host '  部署完成。请打开主程序的「环境设置 → 环境自检」确认 5 项全绿：'
Write-Host '    虚拟声卡 / 默认录音设备=CABLE Output / WinUHid 驱动 / 按键受保护 / 语音桥'
Write-Host ''
Write-Host '  日常用法：按住遥控器【语音键】对着遥控器说话即可（会自动注入右 Alt）。'
Write-Host '  按最底部【方块键】可循环切换配置组（默认组 ↔ WorkBuddy 快捷键组）。'
Write-Host '══════════════════════════════════════════════════════════'
