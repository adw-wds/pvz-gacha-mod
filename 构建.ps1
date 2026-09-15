# 一键构建：mod → 引用校验 → 离线测试 → 收集产物
#
# 注意：产物必须落到「交付根\MOD」（即 源码\..\MOD），而不是 源码\MOD。
# 安装器是从交付根的 MOD 目录取 DLL 的；之前写错目录导致构建“成功”但游戏里装的一直是旧版。
$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot                            # ...\修改器\源码
$repo    = Split-Path -Parent $root                 # ...\修改器
$mod     = Join-Path $repo "MOD"                    # ...\修改器\MOD
$builtDll = Join-Path $root "PvzGachaMod\bin\Release\net472\PvzGachaMod.dll"

Write-Host "1/5 构建 mod" -ForegroundColor Cyan

# 每次构建写入时间戳常量：mod 会把它上报到状态文件，面板上能直接看到游戏里跑的是哪一版。
# （因为吃过一次亏：产物写错目录，构建一直“成功”但游戏里跑的是两天前的旧 DLL）
$stamp = Get-Date -Format "yyMMdd-HHmm"
$stampFile = Join-Path $root "PvzGachaMod\BuildStamp.g.cs"
$stampCode = @"
// 由 源码\构建.ps1 自动生成，请勿手改
namespace PvzGachaMod
{
    internal static class BuildStamp
    {
        public const string Value = "$stamp";
    }
}
"@
[System.IO.File]::WriteAllText($stampFile, $stampCode, (New-Object System.Text.UTF8Encoding($false)))

dotnet build (Join-Path $root "PvzGachaMod\PvzGachaMod.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "mod 构建失败" }

Write-Host "2/5 引用校验（mod 用到的每个成员都必须在游戏程序集里存在）" -ForegroundColor Cyan
dotnet run --project (Join-Path $root "API检查\API检查.csproj") -c Release -- Verify $builtDll
if ($LASTEXITCODE -ne 0) { throw "引用校验未通过：mod 里用到了游戏没有的成员" }

Write-Host "3/5 离线测试（含与运行中 mod 的实机往返，游戏没开会自动跳过）" -ForegroundColor Cyan
dotnet run --project (Join-Path $root "测试\测试.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "测试未通过" }

Write-Host "4/5 收集产物 → $mod" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $mod | Out-Null
Copy-Item $builtDll $mod -Force

# 自检：比哈希。光看“复制完成”不够 —— 之前就是因为目标目录算错，静默装了两天的旧 DLL。
$srcHash = (Get-FileHash $builtDll -Algorithm SHA256).Hash
$dstHash = (Get-FileHash (Join-Path $mod "PvzGachaMod.dll") -Algorithm SHA256).Hash
if ($srcHash -ne $dstHash) { throw "产物复制校验失败：$mod\PvzGachaMod.dll 与刚构建的不一致" }

Write-Host "5/5 自检通过：$mod\PvzGachaMod.dll（$([int]((Get-Item (Join-Path $mod 'PvzGachaMod.dll')).Length / 1024)) KB，构建号 $stamp，哈希 $($dstHash.Substring(0,12))…）" -ForegroundColor Green
Write-Host "提示：要让游戏用上，还需运行 工具\安装器\安装器.exe install（或 安装.cmd）。" -ForegroundColor DarkGray
