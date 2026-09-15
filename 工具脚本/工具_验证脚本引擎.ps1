# 脚本引擎端到端验证：下发一段真脚本，看游戏是否逐条执行完
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$CMD = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x547D) + ([char]0x4EE4) + '.json')
$STATE = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')

Add-Type -Namespace SG -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@

$p = Get-Process $GAME -ErrorAction SilentlyContinue
if ($p) {
    [SG.W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
    [SG.W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
} else { 'game not running'; exit 1 }

# 一段包含 set / log / wait / if / repeat 的脚本
# 注意源码里的换行必须转义成 \n，否则会把「按行分隔」的命令队列拆坏
$script = 'log 脚本自检开始\nset system.speed 3\nwait 600\nif system.speed == 3\nlog 三倍速已生效\nend\nrepeat 2\nlog 重复体\nend\nset system.speed 1\nlog 脚本自检结束'
$json = '{"script":"' + $script + '"}'

[System.IO.File]::WriteAllText($CMD, $json, (New-Object System.Text.UTF8Encoding($false)))
"已下发脚本，等它跑完…"
Start-Sleep -Seconds 8

$j = Get-Content $STATE -Encoding UTF8 -Raw

function Field($t, $pat) {
    $m = [regex]::Match($t, $pat)
    if ($m.Success) { return $m.Groups[1].Value }
    return '(not found)'
}

"script.running  = " + (Field $j '"script":\{[^}]*"running":(\w+)')
"script.executed = " + (Field $j '"script":\{[^}]*"line":\d+,"executed":(\d+)')
"script.status   = " + (Field $j '"script":\{[^}]*"status":"([^"]*)"')
"最后 speed 值   = " + (Field $j '"system\.speed"[^}]*"value":([\d.]+)')
"commandsHandled = " + (Field $j '"commandsHandled":(\d+)')

"`n=== mod 日志里的脚本痕迹 ==="
$LOG = Join-Path $full 'PvzGachaMod.log'
if (Test-Path $LOG) { Get-Content $LOG -Encoding UTF8 | Select-String -Pattern '脚本' | Select-Object -Last 8 | ForEach-Object { $_.Line.Trim() } }
