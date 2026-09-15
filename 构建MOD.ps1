$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$plugins = Join-Path $root "..\MOD\BepInEx\plugins"

dotnet build (Join-Path $root "PvzGachaMod\PvzGachaMod.csproj") -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

New-Item -ItemType Directory -Force -Path $plugins | Out-Null
Copy-Item (Join-Path $root "PvzGachaMod\bin\Release\netstandard2.0\PvzGachaMod.dll") $plugins -Force
Write-Host "已复制到 $plugins" -ForegroundColor Green
