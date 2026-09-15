# 实测「游戏后台运行」：开开关 → 让游戏失焦 → 看状态文件是否还在刷新
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$CMD = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x547D) + ([char]0x4EE4) + '.json')
$STATE = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')

Add-Type -Namespace BK -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@

function Sample($tag) {
    $t = (Get-Item $STATE).LastWriteTime
    $j = Get-Content $STATE -Encoding UTF8 -Raw
    $sw = [regex]::Match($j, '"bgSwallowed":(\d+)').Groups[1].Value
    $ins = [regex]::Match($j, '"bgInstalled":(\d+)').Groups[1].Value
    "$tag  age=$([int]((Get-Date) - $t).TotalSeconds)s  挂钩=$ins  已吞消息=$sw"
}

$p = Get-Process $GAME -ErrorAction SilentlyContinue
if (-not $p) { 'game not running'; exit 1 }

# 先聚焦让游戏把开关吃进去
[BK.W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
[BK.W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Seconds 2

[System.IO.File]::WriteAllText($CMD, '{"key":"system.background","value":true}', (New-Object System.Text.UTF8Encoding($false)))
"已下发「游戏后台运行 = 开」，等它装挂钩…"
Start-Sleep -Seconds 4
Sample '开启后(有焦点)'

# 关键一步：把焦点交给别的窗口（这里用 explorer 的桌面），游戏应当继续跑
$shell = New-Object -ComObject WScript.Shell
$shell.AppActivate('Program Manager') | Out-Null
Start-Sleep -Milliseconds 800
"`n已把焦点移走，开始观察 10 秒（age 若一直很小 = 后台运行成功）"
foreach ($i in 1..5) {
    Start-Sleep -Seconds 2
    Sample "失焦 +$($i*2)s"
}
