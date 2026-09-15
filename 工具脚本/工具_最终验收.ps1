# 最终验收：卸载→确认还原 →安装→启动游戏→确认能跑且浮层没了
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$gameDir = $env:GACHA_GAME_DIR
$data = Join-Path $gameDir '抽卡版PVZ_Data'
$managed = Join-Path $data 'Managed'
$installer = Join-Path $root '工具\安装器\安装器.exe'
$exe = Join-Path $gameDir '抽卡版PVZ.exe'

$procName = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + 'PVZ'
$saveDir = Join-Path (Join-Path $env:USERPROFILE 'AppData\LocalLow\MiaoDouzi') $procName
$STATE = Join-Path $saveDir (([char]0x4FEE) + ([char]0x6539) + ([char]0x5668) + ([char]0x72B6) + ([char]0x6001) + '.json')

function Step($t) { "`n=== $t ===" }
function HasHooks {
    # 用注入器的 verify 判定（比自己扫字节可靠）
    $out = & $installer verify 2>&1 | Out-String
    return ($out -match '4/4') -or ($out -notmatch '未注入 [1-9]')
}

Get-Process $procName -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Step '1. 卸载'
& $installer uninstall
"mod dll 还在 = $(Test-Path (Join-Path $managed 'PvzGachaMod.dll'))"
"ACS 备份还在 = $(Test-Path (Join-Path $managed 'Assembly-CSharp.dll.orig'))"
"清单备份还在 = $([bool](Get-ChildItem $data -Filter '*.json.orig' -ErrorAction SilentlyContinue))"

# 反编译工具（可选）：自行准备 ilspycmd，并在此填路径
# $ILSPY = 'ilspycmd.dll 的路径'
$hooks = dotnet $ilspy -t shangdian (Join-Path $managed 'Assembly-CSharp.dll') 2>$null | Select-String 'Hooks\.'
"程序集里还有注入 = $([bool]$hooks)   （期望 False）"

Step '2. 重新安装'
& $installer install

Step '3. 启动游戏'
Remove-Item $STATE -Force -ErrorAction SilentlyContinue
Start-Process $exe -WorkingDirectory $gameDir | Out-Null
Start-Sleep -Seconds 24
"游戏进程存活 = $([bool](Get-Process $procName -ErrorAction SilentlyContinue))"
if (Test-Path $STATE) {
    $j = Get-Content $STATE -Encoding UTF8 -Raw
    "modVersion = " + ([regex]::Match($j, '"modVersion":"([^"]*)"').Groups[1].Value)
    "能力项数   = " + ([regex]::Matches($j, '"name":"')).Count
    "含 diag    = " + $j.Contains('"diag"')
    "含 overlay = " + $j.Contains('system.overlay')
    "含热键能力 = " + $j.Contains('"name":"' + [char]0x70ED + [char]0x952E + '"')
} else {
    "!! 没有状态文件"
}
