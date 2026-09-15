# 在游戏程序集里搜 UTF-16LE 字符串（.NET 的 #US 堆就是 UTF-16），用来定位某个浮层是谁画的
param([string]$Text = "显血", [string]$Dir = Join-Path $env:GACHA_GAME_DIR "抽卡版PVZ_Data\Managed")

$needle = [System.Text.Encoding]::Unicode.GetBytes($Text)
$files = Get-ChildItem $Dir -Include *.dll, *.exe -File -Recurse -ErrorAction SilentlyContinue

foreach ($f in $files) {
    try {
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
    } catch { continue }

    for ($i = 0; $i -le $bytes.Length - $needle.Length; $i++) {
        if ($bytes[$i] -ne $needle[0]) { continue }
        $hit = $true
        for ($j = 1; $j -lt $needle.Length; $j++) {
            if ($bytes[$i + $j] -ne $needle[$j]) { $hit = $false; break }
        }
        if ($hit) { "命中: $($f.Name) @ offset $i"; break }
    }
}
"搜完 $($files.Count) 个文件（关键字：$Text）"
