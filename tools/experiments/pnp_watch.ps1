# 监听 PnP 设备节点变化（用于观察按语音键时是否出现 BLE/RFCOMM 等新通道）
# 用法: powershell -File pnp_watch.ps1 -Seconds 180 -Out F:\vibe\pnp_watch.log
param(
    [int]$Seconds = 180,
    [string]$Out = "F:\vibe\pnp_watch.log"
)

$log = "$Out"
"=== pnp watch start $(Get-Date -Format 'HH:mm:ss') ($Seconds s) ===" | Out-File -FilePath $log -Encoding UTF8

function Snap {
    $set = @{}
    foreach ($d in (Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue)) {
        $set[$d.InstanceId] = "$($d.Class)|$($d.FriendlyName)"
    }
    return $set
}

$base = Snap
"baseline devices: $($base.Count)" | Out-File -FilePath $log -Append -Encoding UTF8

$end = (Get-Date).AddSeconds($Seconds)
$seen = @{}
while ((Get-Date) -lt $end) {
    Start-Sleep -Milliseconds 700
    $now = Snap
    foreach ($k in $now.Keys) {
        if (-not $base.ContainsKey($k) -and -not $seen.ContainsKey($k)) {
            $seen[$k] = $true
            "[$(Get-Date -Format 'HH:mm:ss.fff')] NEW DEVICE: $($now[$k])  <<$k>>" | Out-File -FilePath $log -Append -Encoding UTF8
        }
    }
    foreach ($k in @($base.Keys)) {
        if (-not $now.ContainsKey($k) -and -not $seen.ContainsKey("GONE:$k")) {
            $seen["GONE:$k"] = $true
            "[$(Get-Date -Format 'HH:mm:ss.fff')] GONE DEVICE: $($base[$k])  <<$k>>" | Out-File -FilePath $log -Append -Encoding UTF8
        }
    }
}
"=== pnp watch end $(Get-Date -Format 'HH:mm:ss')；变化数: $($seen.Count) ===" | Out-File -FilePath $log -Append -Encoding UTF8
