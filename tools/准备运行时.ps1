# ============================================================================
#  IPRC1000 遥控器适配器 —— 准备 .NET 桌面运行时
#
#  职责单一：**只做检测与安装，不启动任何程序**。
#  这样它能被两个入口复用，不会出现两份实现漂移：
#    · 启动.cmd        —— 用户双击的入口，装好后由它去启动主程序
#    · 一键部署.ps1    —— 部署流程的第 ⓪ 步
#
#  为什么需要它：
#    主程序是「框架依赖」发布 —— 安装包只有约 4MB，不打包 140MB 的 .NET 运行时，
#    而是共用系统已装的（微软推荐做法）。代价是机器上必须有它。
#
#  判定规则：
#    只看 Microsoft.WindowsDesktop.App（WinForms 必需，光有 NETCore.App 不够），
#    主版本 >= 8 即通过。csproj 里设了 RollForward=Major，
#    所以 .NET 8/9/10/11 的机器都不会被漏掉。
#
#  安装来源（优先级）：
#    ① 包内离线安装器  assets\dotnet\windowsdesktop-runtime-*-win-x64.exe
#    ② 联网下载        https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe
#
#  退出码：0 = 运行时已就绪（含"刚装好"）；1 = 仍未就绪；2 = 用法错误
#
#  用法：
#    powershell -NoProfile -ExecutionPolicy Bypass -File 准备运行时.ps1
#    powershell -NoProfile -ExecutionPolicy Bypass -File 准备运行时.ps1 -CheckOnly   # 只检测不安装
#
#  测试：检测逻辑抽成了可注入根路径的纯函数，可脱离环境变量单独测：
#    . .\准备运行时.ps1 -LibraryOnly
#    Get-DesktopRuntimeVersion -Roots @('C:\Program Files\dotnet')
# ============================================================================
[CmdletBinding()]
param(
    # 只检测并报告，绝不安装（给自动化测试 / 一键部署预检用）
    [switch]$CheckOnly,
    # 只加载函数定义、不跑主流程（供测试 dot-source）
    [switch]$LibraryOnly,
    # 一切正常时不输出任何内容（启动.cmd 走这条路，避免每次启动闪一行字）
    [switch]$Quiet,
    # 目标程序目录（默认自动定位，用于找包内离线安装器）
    [string]$AppDir,
    # 覆盖检测用的 dotnet 根目录列表（默认从环境变量推导；测试时注入）
    [string[]]$Roots
)

$ErrorActionPreference = 'Stop'

# ══════════════════════ 纯函数区（无副作用，可单测） ══════════════════════

# 推导要搜索的 dotnet 根目录
function Get-DotNetRoots {
    $list = @()
    if ($env:DOTNET_ROOT) { $list += $env:DOTNET_ROOT }
    if (${env:ProgramFiles})     { $list += (Join-Path ${env:ProgramFiles} 'dotnet') }
    if (${env:ProgramFiles(x86)}) { $list += (Join-Path ${env:ProgramFiles(x86)} 'dotnet') }
    return ($list | Where-Object { $_ } | Select-Object -Unique)
}

# 从若干根目录里找「主版本 >= 8 的 Microsoft.WindowsDesktop.App」，返回最高版本号或 $null
function Get-DesktopRuntimeVersion {
    param([string[]]$Roots)

    if (-not $Roots) { $Roots = Get-DotNetRoots }

    $found = @()
    foreach ($r in $Roots) {
        if (-not $r) { continue }
        $shared = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (-not (Test-Path $shared)) { continue }
        foreach ($d in (Get-ChildItem $shared -Directory -ErrorAction SilentlyContinue)) {
            # 目录名形如 8.0.29 / 10.0.12，取主版本号比对
            $m = [regex]::Match($d.Name, '^(\d+)\.')
            if (-not $m.Success) { continue }
            if ([int]$m.Groups[1].Value -ge 8) { $found += $d.Name }
        }
    }
    if ($found.Count -eq 0) { return $null }
    # 取最高的那个。两个坑：
    #  ① 必须按「主版本数字」排序 —— 字符串排序会把 "10.0.12" 排在 "8.0.29" 前面；
    #  ② 必须用 @() 强制成数组再索引 —— 只有一个元素时管道结果是字符串标量，
    #     $x[0] 会变成「取第一个字符」（"10.0.12"[0] = "1"）。这是最常见的情形，别踩。
    $sorted = @($found | Sort-Object { [int]([regex]::Match($_, '^(\d+)').Groups[1].Value) } -Descending)
    return $sorted[0]
}

# ══════════════════════ 以下有副作用，测试时用 -LibraryOnly 跳过 ══════════════════════
if ($LibraryOnly) { return }

[Console]::OutputEncoding = [Text.Encoding]::UTF8

# 这次安装是否已经装完但需要重启（返回码 3010）
$script:NeedsReboot = $false

function Info($m) { Write-Host "  $m" }
function Ok($m)   { Write-Host "  [OK] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [!!] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "  [XX] $m" -ForegroundColor Red }

# ── 定位程序目录（用于找包内离线安装器）──────────────────────────────────────
if (-not $AppDir) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
    foreach ($cand in @($here, (Split-Path -Parent $here))) {
        if ($cand -and (Test-Path (Join-Path $cand 'IPRC1000 Adapter.exe'))) { $AppDir = $cand; break }
    }
    if (-not $AppDir) { $AppDir = $here }
}

# ── 找安装器：包内优先，其次联网 ─────────────────────────────────────────────
function Get-Installer {
    $dotnetDir = Join-Path $AppDir 'assets\dotnet'
    if (Test-Path $dotnetDir) {
        $local = Get-ChildItem $dotnetDir -Filter 'windowsdesktop-runtime-*-win-x64.exe' -ErrorAction SilentlyContinue |
                 Sort-Object Name -Descending | Select-Object -First 1
        if ($local) { return @{ Path = $local.FullName; Source = '包内离线安装器' } }
    }
    $dst = Join-Path $env:TEMP 'windowsdesktop-runtime-win-x64.exe'
    $url = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
    Info '包内没有离线安装器，联网下载（约 55MB，微软官方 aka.ms）...'
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        & curl.exe -L --fail --output $dst $url 2>&1 | Out-Null
    } else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $dst
    }
    if (-not (Test-Path $dst)) { return $null }
    return @{ Path = $dst; Source = '官方下载' }
}

# ══════════════════════════════ 主流程 ══════════════════════════════
if (-not $Quiet) { Write-Host '' }
$ver = Get-DesktopRuntimeVersion -Roots $Roots
if ($ver) {
    if (-not $Quiet) { Ok ".NET 桌面运行时已就绪：$ver" }
    exit 0
}

Warn '本机缺少 .NET 桌面运行时'
Info '主程序为了把安装包控制在约 4MB，没有打包运行时，而是共用系统这一份。'

if ($CheckOnly) {
    Info '（-CheckOnly：只检测，不安装）'
    exit 1
}

# 安装需要管理员权限
$isAdmin = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Fail '安装需要管理员权限。请右键「启动.cmd」或桌面快捷方式，选「以管理员身份运行」。'
    exit 1
}

$inst = Get-Installer
if (-not $inst) {
    Fail '拿不到安装器（下载失败）。请检查网络，或手动安装后重试：'
    Info 'https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0'
    exit 1
}

Info "正在安装（来源：$($inst.Source)，静默模式，约需 1 分钟）..."
$p = Start-Process -FilePath $inst.Path -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
Info "安装程序返回码 $($p.ExitCode)   (0 = 成功，3010 = 装好但需重启)"
if ($p.ExitCode -eq 3010) { $script:NeedsReboot = $true }

$ver = Get-DesktopRuntimeVersion -Roots $Roots
if ($ver) {
    Ok ".NET 桌面运行时已就绪：$ver"
    if ($script:NeedsReboot) { Warn '系统提示需要重启才能生效，建议重启后再用。' }
    exit 0
}

Fail '装完仍未检测到运行时 —— 可能被安全软件拦下，或要重启后才生效。'
Info '请重启电脑后再运行一次。'
exit 1
