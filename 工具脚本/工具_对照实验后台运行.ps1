# 对照实验：关掉「后台运行」开关 → 失焦 → 主循环是否停（停 = 证明挂钩才是原因）
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$CMD = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x547D) + ([char]0x4EE4) + '.json')
$STATE = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')

Add-Type -Namespace CK -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@

$p = Get-Process $GAME -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { 'game not running'; exit 1 }
$gameHwnd = $p.MainWindowHandle

function Report($tag) {
    $isGame = ([CK.W]::GetForegroundWindow() -eq $gameHwnd)
    $t = (Get-Item $STATE).LastWriteTime
    $j = Get-Content $STATE -Encoding UTF8 -Raw
    $ins = [regex]::Match($j, '"bgInstalled":(\d+)').Groups[1].Value
    "$tag  前台是游戏=$isGame  挂钩=$ins  age=$([int]((Get-Date) - $t).TotalSeconds)s"
}

# 关掉开关（先聚焦让游戏收命令）
[CK.W]::ShowWindow($gameHwnd, 9) | Out-Null
[CK.W]::SetForegroundWindow($gameHwnd) | Out-Null
Start-Sleep -Seconds 2
[System.IO.File]::WriteAllText($CMD, '{"key":"system.background","value":false}', (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Seconds 4
Report '已关闭开关'

# 失焦
$conHost = (Get-Process -Id $PID).MainWindowHandle
if ($conHost -ne 0) { [CK.W]::SetForegroundWindow($conHost) | Out-Null }
Start-Sleep -Seconds 2
Report '失焦起点'

"`n--- 关闭状态下失焦 12 秒（age 若越来越大 = 主循环真的会停 = 挂钩确实是原因）---"
foreach ($i in 1..6) { Start-Sleep -Seconds 2; Report "  +$($i*2)s" }

# 恢复：重新打开
[CK.W]::ShowWindow($gameHwnd, 9) | Out-Null
[CK.W]::SetForegroundWindow($gameHwnd) | Out-Null
Start-Sleep -Seconds 2
[System.IO.File]::WriteAllText($CMD, '{"key":"system.background","value":true}', (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Seconds 4
Report '`n已恢复开启'
if ($conHost -ne 0) { [CK.W]::SetForegroundWindow($conHost) | Out-Null }
Start-Sleep -Seconds 2
foreach ($i in 1..3) { Start-Sleep -Seconds 2; Report "  再失焦 +$($i*2)s" }
