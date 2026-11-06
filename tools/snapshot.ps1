# IPRC1000 environment snapshot (READ-ONLY, changes nothing)
#
# Run on BOTH machines (dev = known-good, customer = broken), then diff the two txt files:
#   powershell -NoProfile -ExecutionPolicy Bypass -File snapshot.ps1
#
# Output: snapshot-<COMPUTERNAME>.txt next to this script. Format is KEY=VALUE, one per line,
# so `fc /n a.txt b.txt` or `diff a.txt b.txt` gives a clean side-by-side.

param([string]$Out)

$ErrorActionPreference = 'SilentlyContinue'
if (-not $Out) { $Out = Join-Path $PSScriptRoot ("snapshot-" + $env:COMPUTERNAME + ".txt") }

$script:lines = New-Object System.Collections.Generic.List[string]
function A([string]$k, $v) { $script:lines.Add([string]$k + "=" + [string]$v) }

# ---------- host ----------
$os = Get-CimInstance Win32_OperatingSystem
A "host" $env:COMPUTERNAME
A "os" ($os.Caption + " build " + $os.BuildNumber)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
A "admin" $isAdmin

# ---------- bluetooth adapter + driver ----------
$bt = @(Get-PnpDevice -Class Bluetooth)
$adapters = @($bt | Where-Object { $_.InstanceId -match '^USB\\|^PCI\\|^BTH\\' -or $_.FriendlyName -match 'Adapter|适配器' })
A "bt_adapter_count" $adapters.Count
$i = 0
foreach ($d in $adapters) {
    A ("bt_adapter[" + $i + "]") ($d.Status + " | " + $d.FriendlyName + " | " + $d.InstanceId)
    $i++
}
$drv = @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -match 'Bluetooth' })
foreach ($d in $drv) {
    A "bt_driver" ($d.DeviceName + " | ver=" + $d.DriverVersion + " | inf=" + $d.InfName)
}

# ---------- BTHPORT: pairing record + SDP cache ----------
# SDP cache lives in  Devices\<mac>\ServicesFor<localAdapterMac>\{serviceGuid}\...
# This is THE key difference: a record without ServicesFor means Windows never ran
# service discovery, so no HID node is created and PSM 0x0011 has no listener.
$devRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices'
$pairIdx = 0
foreach ($k in (Get-ChildItem $devRoot)) {
    $p = Get-ItemProperty $k.PSPath
    $nm = ''
    if ($p.Name) { $nm = [Text.Encoding]::ASCII.GetString($p.Name).Trim([char]0) }
    if (-not ($nm -match 'IPRC' -or $p.VID -eq 2652)) { continue }
    $sf = @(Get-ChildItem $k.PSPath | Where-Object { $_.PSChildName -like 'ServicesFor*' })
    $recs = 0
    $hasHid = 'no'
    foreach ($s in $sf) {
        $g = @(Get-ChildItem $s.PSPath)
        $recs += $g.Count
        foreach ($x in $g) { if ($x.PSChildName -like '*00001124*') { $hasHid = 'yes' } }
    }
    A ("btpair[" + $pairIdx + "].mac") $k.PSChildName
    A ("btpair[" + $pairIdx + "].name") $nm
    A ("btpair[" + $pairIdx + "].ids") ("vid=0x" + ('{0:X}' -f $p.VID) + " pid=0x" + ('{0:X}' -f $p.PID))
    A ("btpair[" + $pairIdx + "].servicesForKeys") $sf.Count
    A ("btpair[" + $pairIdx + "].sdpRecords") $recs
    A ("btpair[" + $pairIdx + "].hasHIDservice") $hasHid
    $pairIdx++
}
A "btpair_count" $pairIdx

# ---------- Enum\BTHENUM: is HidBth bound? ----------
$hidSvc = 'no'
$hidSvcDetail = 'none'
foreach ($k in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM' | Where-Object { $_.PSChildName -match '00001124' })) {
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        $p = Get-ItemProperty $inst.PSPath
        $hidSvcDetail = [string]$p.Service
        if ($p.Service -eq 'HidBth') { $hidSvc = 'yes' }
    }
}
A "bthenum_hid_bound" $hidSvc
A "bthenum_hid_service" $hidSvcDetail

# ---------- Enum\HID: which driver is bound (this decides "types letters" or not) ----------
$n = 0
$svc = 'none'
$cls = 'none'
$clsGuid = 'none'
foreach ($k in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID' | Where-Object { $_.PSChildName -match '0A5C|8502' })) {
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        $n++
        $p = Get-ItemProperty $inst.PSPath
        $svc = [string]$p.Service
        $cls = [string]$p.Class
        $clsGuid = [string]$p.ClassGUID
    }
}
A "enumhid_count" $n
A "enumhid_service" $(if ($svc -eq '') { '<empty>' } else { $svc })
A "enumhid_class" $(if ($cls -eq '') { '<empty>' } else { $cls })
A "enumhid_classguid" $clsGuid
# kbdhid => system types letters; empty/mshidkmdf => HID device but NO Raw Input keyboard

# ---------- VB-CABLE ----------
$cable = @(Get-CimInstance Win32_SoundDevice | Where-Object { $_.Name -match 'CABLE' })
A "cable_present" $(if ($cable.Count -gt 0) { 'yes' } else { 'no' })
A "cable_count" $cable.Count
foreach ($c in $cable) { A ("cable[" + $c.Name + "]") $c.Status }

# ---------- WinUHid virtual HID enumerator ----------
A "winuhid_node" $(if (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\WINUHID_VIRTUAL_HID_ENUMERATOR') { 'yes' } else { 'no' })

# ---------- recent HidBth events ----------
$ev = @(Get-WinEvent -LogName System -MaxEvents 300 | Where-Object { $_.ProviderName -match 'HidBth' } | Select-Object -First 5)
A "hidbth_event_count" $ev.Count
foreach ($e in $ev) {
    $m = ($e.Message -replace '\s+', ' ')
    if ($m.Length -gt 70) { $m = $m.Substring(0, 70) }
    A "hidbth_event" ($e.TimeCreated.ToString('MM-dd HH:mm:ss') + " id=" + $e.Id + " " + $m)
}

$script:lines | Set-Content -Encoding UTF8 $Out
Get-Content $Out | Write-Host
Write-Host ""
Write-Host "Saved to $Out"
Write-Host "Next: run the SAME script on the other machine, then diff the two files."
