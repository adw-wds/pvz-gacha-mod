# 验证「设置持久化」：设一个值 → 关游戏 → 重开 → 值应该还在
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$CMD = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x547D) + ([char]0x4EE4) + '.json')
$STATE = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')
$SETTINGS = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x8BBE) + ([char]0x7F6E) + '.json')
$EXE = Join-Path $env:GACHA_GAME_DIR '抽卡版PVZ.exe'
$GAMEDIR = $env:GACHA_GAME_DIR

function SpeedOf($path) {
    if (-not (Test-Path $path)) { return '(no state)' }
    $j = Get-Content $path -Encoding UTF8 -Raw
    $m = [regex]::Match($j, '"battle\.speed"[^}]*"value":([\d.]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return '(not found)'
}

function FocusGame {
    $p = Get-Process $GAME -ErrorAction SilentlyContinue
    if (-not $p) { return }
    Add-Type -Namespace FF -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@ -ErrorAction SilentlyContinue
    [FF.W]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
    [FF.W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
}

"=== 第 1 步：启动游戏，把速度设成 3 ==="
Get-Process $GAME -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $SETTINGS) { Remove-Item $SETTINGS -Force }
Start-Sleep -Seconds 2
Start-Process $EXE -WorkingDirectory $GAMEDIR | Out-Null
Start-Sleep -Seconds 22
FocusGame
[System.IO.File]::WriteAllText($CMD, '{"key":"battle.speed","value":3}', (New-Object System.Text.UTF8Encoding($false)))
Start-Sleep -Seconds 4
"状态文件里的 speed = $(SpeedOf $STATE)"
"设置文件存在 = $(Test-Path $SETTINGS)"
if (Test-Path $SETTINGS) { "设置文件内容 = $(Get-Content $SETTINGS -Encoding UTF8 -Raw)" }

"`n=== 第 2 步：关游戏再重开，看值是否保留 ==="
Get-Process $GAME -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
Remove-Item $STATE -Force -ErrorAction SilentlyContinue
Start-Process $EXE -WorkingDirectory $GAMEDIR | Out-Null
Start-Sleep -Seconds 22
FocusGame
"重开后 speed = $(SpeedOf $STATE)   （期望 3）"
