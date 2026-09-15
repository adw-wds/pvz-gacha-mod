param(
    [string]$GameDir = $env:GACHA_GAME_DIR,
    [switch]$Unregister
)

$ErrorActionPreference = "Stop"
$data = Join-Path $GameDir "抽卡版PVZ_Data"
$sa = Join-Path $data "ScriptingAssemblies.json"
$ri = Join-Path $data "RuntimeInitializeOnLoads.json"
$dllName = "PvzGachaMod.dll"
$asmName = "PvzGachaMod"

function Save-Json($path, $obj) {
    $text = $obj | ConvertTo-Json -Depth 20 -Compress
    # 必须写无 BOM 的 UTF-8（Unity 的解析器对 BOM 不可靠）
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

foreach ($f in @($sa, $ri)) {
    if (-not (Test-Path $f)) { throw "找不到 $f" }
    if (-not (Test-Path "$f.orig")) { Copy-Item $f "$f.orig"; Write-Host "已备份 $([System.IO.Path]::GetFileName($f)) → .orig" }
}

# ---- 1) ScriptingAssemblies.json：让 Unity 启动时预加载我们的程序集 ----
$json = Get-Content $sa -Raw | ConvertFrom-Json
$names = New-Object System.Collections.Generic.List[string]
foreach ($n in $json.names) { $names.Add($n) }

if ($Unregister) {
    if ($names.Contains($dllName)) { $names.Remove($dllName) | Out-Null }
} else {
    if (-not $names.Contains($dllName)) { $names.Add($dllName) }
}
$json.names = $names.ToArray()
Save-Json $sa $json
Write-Host "ScriptingAssemblies.json：当前 $($json.names.Count) 项，包含 $dllName = $($json.names -contains $dllName)"

# ---- 2) RuntimeInitializeOnLoads.json：登记启动时调用我们的静态方法 ----
$rj = Get-Content $ri -Raw | ConvertFrom-Json
$root = New-Object System.Collections.Generic.List[object]
foreach ($e in $rj.root) { $root.Add($e) }

$found = $false
foreach ($e in $root) { if ($e.assemblyName -eq $asmName) { $found = $true } }

if ($Unregister) {
    if ($found) {
        $kept = New-Object System.Collections.Generic.List[object]
        foreach ($e in $root) { if ($e.assemblyName -ne $asmName) { $kept.Add($e) } }
        $root = $kept
    }
    $found = $false
} elseif (-not $found) {
    $root.Add([pscustomobject]@{
        assemblyName = $asmName
        nameSpace    = "PvzGachaMod"
        className    = "ModBootstrap"
        methodName   = "Init"
        loadTypes    = 2      # 2 = AfterSceneLoad
        isUnityClass = $false
    })
    $found = $true
}

$rj.root = $root.ToArray()
Save-Json $ri $rj
Write-Host "RuntimeInitializeOnLoads.json：当前 $($rj.root.Count) 项，已登记 $asmName = $found"
