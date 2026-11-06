# IPRC1000 Bluetooth pairing reset (read-only by default)
#
# Why: Windows stores one pairing in THREE independent places. The "Remove device"
# button in Settings only removes the PnP nodes (2 and 3). The BTHPORT record (1)
# survives, so the next pairing is treated as a RECONNECT, not a new pairing, and
# Windows skips service discovery (SDP). Result: no {00001124} HID node, no HidBth
# binding, PSM 0x0011 has no listener, remote gets REFUSED_PSM forever.
#
#   1) HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\<mac>
#        + ServicesFor<localAdapterMac>   <- the SDP cache lives here
#   2) HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\Dev_<MAC>
#   3) HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\{serviceGuid}_..._<MAC>
#
# Usage (admin):
#   powershell -NoProfile -ExecutionPolicy Bypass -File reset-bt-device.ps1
#       -> read-only report
#   powershell -NoProfile -ExecutionPolicy Bypass -File reset-bt-device.ps1 -Mac 20c047f313d6 -Apply
#       -> export a .reg backup, then delete the three places, then tell you to reboot

param(
    [string]$Mac = '20c047f313d6',
    [switch]$Apply
)

$ErrorActionPreference = 'SilentlyContinue'
$macU = $Mac.ToUpper()
$out = Join-Path $PSScriptRoot ("reset-bt-" + $Mac + ".txt")
"IPRC1000 pairing reset report  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" | Set-Content -Encoding UTF8 $out
"device MAC = $Mac   mode = $(if ($Apply) {'APPLY'} else {'READ-ONLY'})" | Out-File -FilePath $out -Append -Encoding UTF8
"" | Out-File -FilePath $out -Append -Encoding UTF8
function W([string]$s) { $s | Out-File -FilePath $out -Append -Encoding UTF8 }

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
W "administrator = $admin"
W ""

# ---------- 1) BTHPORT record ----------
$btDev = "HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac"
W "=== 1. BTHPORT pairing record ==="
$hasBt = Test-Path $btDev
if (-not $hasBt) {
    W "  (absent)  <- good; nothing to recycle"
} else {
    $p = Get-ItemProperty $btDev
    $nm = ''
    if ($p.Name) { $nm = [System.Text.Encoding]::ASCII.GetString($p.Name).Trim([char]0) }
    W "  PRESENT   name='$nm'  VID=0x$('{0:X}' -f $p.VID)  PID=0x$('{0:X}' -f $p.PID)"
    W "  FingerprintVersion = $($p.FingerprintVersion)"
    $sf = Get-ChildItem $btDev | Where-Object { $_.PSChildName -like 'ServicesFor*' }
    if (-not $sf) {
        W "  ServicesFor* : NONE   <=== SDP was NEVER cached under any local adapter"
        W "      => Windows treats every re-pair as a reconnect and skips SDP."
        W "      => This is why re-pairing changed nothing."
    } else {
        foreach ($s in $sf) {
            $guids = Get-ChildItem $s.PSPath | ForEach-Object { $_.PSChildName }
            W ("  " + $s.PSChildName + " : " + $guids.Count + " service record(s)")
            foreach ($g in $guids) { W ("      " + $g + $(if ($g -like '*00001124*') { '   <== HID' } else { '' })) }
        }
    }
}
W ""

# ---------- 2) and 3) BTHENUM nodes ----------
W "=== 2/3. Enum\BTHENUM nodes for this MAC ==="
$enumHits = @()
$root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\BTHENUM'
foreach ($k in (Get-ChildItem $root)) {
    $hit = $false
    if ($k.PSChildName -match ('(?i)' + [regex]::Escape($macU))) { $hit = $true }
    elseif ($k.PSChildName -match '(?i)DEV_' -and $k.PSChildName -match ('(?i)' + $macU)) { $hit = $true }
    if (-not $hit) { continue }
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        if ($inst.PSChildName -match ('(?i)' + $macU)) {
            $enumHits += [pscustomobject]@{ Key = $k.PSChildName; Inst = $inst.PSChildName }
        }
    }
}
if ($enumHits.Count -eq 0) {
    W "  (none)  <- no leftover PnP nodes either"
} else {
    foreach ($h in $enumHits) {
        W ("  KEY  " + $h.Key)
        W ("    INST " + $h.Inst)
    }
}
W ""

# ---------- 4) Enum\HID leftovers ----------
W "=== 4. Enum\HID leftovers for VID 0A5C ==="
$hidHits = @()
foreach ($k in (Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\HID' | Where-Object { $_.PSChildName -match '(?i)VID[&_](0002)?0A5C|PID[&_]8502' })) {
    foreach ($inst in (Get-ChildItem $k.PSPath)) {
        $hidHits += [pscustomobject]@{ Key = $k.PSChildName; Inst = $inst.PSChildName }
    }
}
if ($hidHits.Count -eq 0) { W "  (none)" }
foreach ($h in $hidHits) { W ("  " + $h.Key + " \ " + $h.Inst) }
W ""

# ---------- verdict ----------
W "=== verdict ==="
if ($hasBt -and -not $sf) {
    W "  [ROOT CAUSE] BTHPORT record exists but has NO ServicesFor* cache."
    W "  Every re-pair is treated as a reconnect -> SDP skipped -> no HID node."
    W "  Fix: run this script with -Apply, reboot, then pair again."
} elseif ($hasBt -and $sf) {
    $hasHid = $false
    foreach ($s in $sf) { if (Get-ChildItem $s.PSPath | Where-Object { $_.PSChildName -like '*00001124*' }) { $hasHid = $true } }
    if ($hasHid) { W "  [OK] BTHPORT record has a HID service record cached. Host side is fine." }
    else { W "  [ODD] ServicesFor cache exists but has no HID entry. SDP ran but HID was not advertised." }
} elseif (-not $hasBt) {
    W "  [OK] No stale BTHPORT record. A fresh pairing will run SDP normally."
}

# ---------- apply ----------
if ($Apply) {
    W ""
    W "=== applying ==="
    if (-not $admin) {
        W "  [FAIL] need administrator. Re-run this PowerShell as administrator."
        Get-Content $out | Write-Host
        exit 2
    }
    $backup = Join-Path ([Environment]::GetFolderPath('Desktop')) ("bt-backup-" + $Mac + "-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".reg")
    & reg.exe export "HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac" $backup /y | Out-Null
    W "  backup -> $backup"

    if ($hasBt) {
        & reg.exe delete "HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\$Mac" /f | Out-Null
        W "  deleted BTHPORT record"
    }
    foreach ($h in $enumHits) {
        $p = 'HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\' + $h.Key + '\' + $h.Inst
        & reg.exe delete $p /f | Out-Null
        W ('  deleted BTHENUM ' + $h.Key)
    }
    foreach ($h in $hidHits) {
        $p = 'HKLM\SYSTEM\CurrentControlSet\Enum\HID\' + $h.Key + '\' + $h.Inst
        & reg.exe delete $p /f | Out-Null
        W ('  deleted HID ' + $h.Key)
    }
    W ""
    W "  DONE. Next steps:"
    W "    1. reboot (required - the bluetooth stack caches pairing in memory)"
    W "    2. remote: hold [last] + [down] to return to pairable state"
    W "    3. long-press PWR until the LED blinks blue"
    W "    4. Settings -> Bluetooth -> Add device -> IPRC1000"
    W "    5. run tools\diag-bt.ps1 : expect {00001124} + Service=HidBth + records > 0"
}

Get-Content $out | Write-Host
Write-Host ""
Write-Host "Saved to $out"
if ($Apply) { exit 0 }
