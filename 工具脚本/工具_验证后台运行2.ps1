# 严格版后台运行验证：明确确认「游戏已不在前台」，再看主循环是否还在跑
$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi'
$GAME = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$full = Join-Path $dir $GAME
$STATE = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')
$SETTINGS = Join-Path $full (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x8BBE) + ([char]0x7F6E) + '.json')

Add-Type -Namespace SK -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
'@

$p = Get-Process $GAME -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { 'game not running'; exit 1 }
$gameHwnd = $p.MainWindowHandle

function Report($tag) {
    $fg = [SK.W]::GetForegroundWindow()
    $t = (Get-Item $STATE).LastWriteTime
    $j = Get-Content $STATE -Encoding UTF8 -Raw
    $sw = [regex]::Match($j, '"bgSwallowed":(\d+)').Groups[1].Value
    $speed = [regex]::Match($j, '"system\.speed"[^}]*"value":([\d.]+)').Groups[1].Value
    $isGame = ($fg -eq $gameHwnd)
    "$tag  前台是游戏=$isGame  age=$([int]((Get-Date) - $t).TotalSeconds)s  吞消息=$sw  速度=$speed"
}

"游戏窗口句柄 = $gameHwnd"
"设置文件里的 background 开关 = " + (([regex]::Match((Get-Content $SETTINGS -Encoding UTF8 -Raw), '"system\.background":(\w+)')).Groups[1].Value)

# 让游戏跑到前台并保持一会儿
[SK.W]::ShowWindow($gameHwnd, 9) | Out-Null
[SK.W]::SetForegroundWindow($gameHwnd) | Out-Null
Start-Sleep -Seconds 3
Report '基线(游戏在前台)'

# 找一个别的窗口来抢焦点：用任务栏/开始菜单不行，改用 PowerShell 自己的控制台窗口
$conHost = (Get-Process -Id $PID).MainWindowHandle
if ($conHost -ne 0) {
    [SK.W]::SetForegroundWindow($conHost) | Out-Null
} else {
    # 没有控制台窗口时，用最小化再还原的旁路：直接激活任务栏
    $tb = (Get-Process explorer -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1)
    if ($tb) { [SK.W]::SetForegroundWindow($tb.MainWindowHandle) | Out-Null }
}
Start-Sleep -Seconds 2

"`n--- 失焦后的观察（age 持续变小 = 主循环没停 = 后台运行成功）---"
foreach ($i in 1..6) {
    Report "  失焦 +$($i*2)s"
    Start-Sleep -Seconds 2
}
