param([string]$Out = "$env:TEMP\game.png")

# Capture just the game window rect (ASCII only on purpose: PS 5.1 mis-decodes
# UTF-8-without-BOM when the script contains Chinese).

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public struct GameRect { public int L, T, Rr, B; }
public class GameWin {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out GameRect r);
}
'@

$p = Get-Process | Where-Object { $_.ProcessName -like '*PVZ*' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { throw "game window not found" }

$r = New-Object GameRect
[GameWin]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null

$w = $r.Rr - $r.L
$h = $r.B - $r.T
if ($w -le 0 -or $h -le 0) { throw "bad window rect" }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()

Write-Output "saved: $Out  (${w}x${h})  origin=($($r.L),$($r.T))"
