# 发布：把源码编译成「双击即用」的交付目录
#
# 产物（都在修改器根目录下）：
#   安装.cmd / 卸载.cmd          用户入口（指向工具\下的已编译 exe）
#   工具\安装器\安装器.exe        安装/卸载/校验（含 Mono.Cecil，负责 IL 注入）
#   工具\面板\抽卡版修改器.exe      WPF 面板
#   工具\Mono.Cecil.dll           （保留，源码里引用它）
#   MOD\PvzGachaMod.dll           注入游戏的那份 mod
#
# 前置：本机要有 .NET 8 SDK（本机已有 8.0.302）。
# 用法：powershell -ExecutionPolicy Bypass -File 发布.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot          # 修改器根目录
$src  = $PSScriptRoot

function Step($n, $text) { Write-Host "`n[$n] $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. 构建 + 校验 + 测试
Step '1/4' '构建 mod、引用校验、离线测试'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $src '构建.ps1')
if ($LASTEXITCODE -ne 0) { throw '构建流水线未通过，中止发布' }

# ---------------------------------------------------------------- 2. 发布工具
#
# 用「自包含 + 单文件」：拷到任何 Windows 机器上双击就能跑，不需要用户先装 .NET 运行时。
# 代价是体积（面板约 60MB，安装器约 12MB），对本地工具来说值得。
Step '2/4' '发布安装器与面板（免运行时单文件）'
$installerOut = Join-Path $root '工具\安装器'
$panelOut     = Join-Path $root '工具\面板'
foreach ($d in @($installerOut, $panelOut)) {
    if (Test-Path $d) { Remove-Item $d -Recurse -Force }
}

$single = @('-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true', '--self-contained', 'true', '-r', 'win-x64')

dotnet publish (Join-Path $src '安装器\安装器.csproj') -c Release -o $installerOut @single --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw '安装器发布失败' }

dotnet publish (Join-Path $src '修改器面板\修改器面板.csproj') -c Release -o $panelOut @single --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw '面板发布失败' }

# pdb 对最终用户没用；WPF 的单文件里会有几个必须外置的原生库，那些不能删
Get-ChildItem $installerOut, $panelOut -Filter *.pdb -File | Remove-Item -Force -ErrorAction SilentlyContinue

# ---------------------------------------------------------------- 2b. 默认背景视频
#
# 面板启动时按 exe 同目录的 wallpaper.* 找背景。★ 不把素材一起发出去的话，
# 用户装完就是一片纯色，会以为「视频功能根本没做」——踩过这个坑。
#
# 素材存在 资源\ 下而不是直接放 工具\面板\：上面发布时会整目录 Remove-Item 重建，
# 放在产物目录里的文件每次发布都会被清掉。
$resDir = Join-Path $root '资源'
$resVid = Join-Path $resDir 'wallpaper.mp4'
if (Test-Path $resVid) {
    Copy-Item $resVid (Join-Path $panelOut 'wallpaper.mp4') -Force
    Write-Host ('  已放入默认背景视频 wallpaper.mp4（{0:N1} MB）' -f ((Get-Item $resVid).Length / 1MB))
} else {
    Write-Host "  [跳过] 没有默认背景视频：$resVid" -ForegroundColor Yellow
}

$installerExe = Join-Path $installerOut '安装器.exe'
$panelExe     = Join-Path $panelOut '抽卡版修改器.exe'
if (-not (Test-Path $installerExe)) { throw "没生成 $installerExe" }
if (-not (Test-Path $panelExe))     { throw "没生成 $panelExe" }
Write-Host "  安装器：$installerExe"
Write-Host "  面板：  $panelExe"

# ---------------------------------------------------------------- 3. 入口 cmd
Step '3/4' '写入口脚本 安装.cmd / 卸载.cmd'

$installCmd = @'
@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "GAME=%~1"
if "%GAME%"=="" set "GAME=%GACHA_GAME_DIR%"

echo === 抽卡版PVZ 修改器 - 安装 ===
echo 游戏目录：%GAME%
echo.

"%ROOT%工具\安装器\安装器.exe" install "%GAME%"
set "CODE=%ERRORLEVEL%"

echo.
if not "%CODE%"=="0" (
  echo 安装失败（退出码 %CODE%）。请确认游戏目录正确、且游戏已退出。
) else (
  echo 安装完成。启动游戏后，双击「工具\面板\抽卡版修改器.exe」打开面板。
)
pause
endlocal
'@

$uninstallCmd = @'
@echo off
chcp 65001 >nul
setlocal
set "ROOT=%~dp0"
set "GAME=%~1"
if "%GAME%"=="" set "GAME=%GACHA_GAME_DIR%"

echo === 抽卡版PVZ 修改器 - 卸载 ===
echo 游戏目录：%GAME%
echo.

"%ROOT%工具\安装器\安装器.exe" uninstall "%GAME%"
set "CODE=%ERRORLEVEL%"

echo.
if not "%CODE%"=="0" (
  echo 卸载失败（退出码 %CODE%）。
) else (
  echo 已还原游戏原始文件。源码里的 Assembly-CSharp.dll.orig 备份也一并清理了。
)
pause
endlocal
'@

# cmd 文件用 GBK 写出：chcp 65001 只管控制台输出编码，脚本自身含中文时用 UTF-8 会乱码
$gbk = [System.Text.Encoding]::GetEncoding(936)
[System.IO.File]::WriteAllText((Join-Path $root '安装.cmd'),   $installCmd,   $gbk)
[System.IO.File]::WriteAllText((Join-Path $root '卸载.cmd'),   $uninstallCmd, $gbk)
Write-Host '  已写入 安装.cmd / 卸载.cmd'

# ---------------------------------------------------------------- 4. 自检
Step '4/4' '自检'
& $installerExe verify
Write-Host ''
Write-Host '发布完成。交付内容：' -ForegroundColor Green
Write-Host '  安装.cmd / 卸载.cmd'
Write-Host '  工具\安装器\安装器.exe      （免运行时）'
Write-Host '  工具\面板\抽卡版修改器.exe   （免运行时）'
Write-Host '  MOD\PvzGachaMod.dll'
Write-Host '  使用说明.md'
