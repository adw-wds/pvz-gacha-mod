# 端到端验证：直接往命令文件里塞命令，看 mod 是否执行、诊断计数是否变化
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$CMD_NAME = ([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x547D) + ([char]0x4EE4) + '.json'
$STATE_NAME = ([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json'
$cmd = Join-Path $full $CMD_NAME
$state = Join-Path $full $STATE_NAME

Add-Type -Namespace G -Name F -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@

function SendCmd($json) {
    [System.IO.File]::WriteAllText($cmd, $json, (New-Object System.Text.UTF8Encoding($false)))
    Start-Sleep -Milliseconds 1500
}

function Field($j, $pattern) {
    $m = [regex]::Match($j, $pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return '(not found)'
}

# 游戏失焦时主循环停住，所以先把它拉到前台
$p = Get-Process $GAME -ErrorAction SilentlyContinue
if ($p) {
    [G.F]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
    [G.F]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
}

SendCmd '{"key":"battle.speed","value":3}'
SendCmd '{"key":"money.coinValue","value":1000000000}'
SendCmd '{"key":"money.lockCoin","value":true}'
Start-Sleep -Seconds 5

$j = Get-Content $state -Encoding UTF8 -Raw
"commandsHandled = " + (Field $j '"commandsHandled":(\d+)')
"lastResult      = " + (Field $j '"lastResult":"([^"]*)"')
"speed           = " + (Field $j '"battle\.speed"[^}]*"value":([\d.]+)')
"coinValue       = " + (Field $j '"money\.coinValue"[^}]*"value":(\d+)')
"coinWrites      = " + (Field $j '"coinWrites":(\d+)')
"modVersion      = " + (Field $j '"modVersion":"([^"]*)"')
