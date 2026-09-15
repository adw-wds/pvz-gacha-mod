# 把修改器面板窗口拉到前台并截图（用于验证界面）
param([string]$Out = "$env:TEMP\panel.png")

$src = @'
using System;
using System.Runtime.InteropServices;
public static class Fg {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$PANEL = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + ([char]0x4FEE) + ([char]0x6539) + ([char]0x5668)
$p = Get-Process $PANEL -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { 'panel window not found'; exit 1 }

$h = $p.MainWindowHandle
$HWND_NOTOPMOST = [IntPtr](-2)
$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001

[Fg]::ShowWindow($h, 9) | Out-Null
# 关键：不要用 HWND_TOPMOST。那会让窗口永久“总在最前”，用户点别处也压不下去（踩过这个坑）。
# 只取消置顶 + 拉到前台就够了。
[Fg]::SetWindowPos($h, $HWND_NOTOPMOST, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE)) | Out-Null
[Fg]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 1200

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

"saved: $Out"
