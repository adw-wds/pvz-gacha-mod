# 取消修改器面板的「总在最前」状态（截图脚本曾用 HWND_TOPMOST，会留下副作用）
$src = @'
using System;
using System.Runtime.InteropServices;
public static class Top {
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$HWND_NOTOPMOST = [IntPtr](-2)
$SWP_NOMOVE = 0x0002
$SWP_NOSIZE = 0x0001

$PANEL = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + ([char]0x4FEE) + ([char]0x6539) + ([char]0x5668)
$procs = Get-Process $PANEL -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }
if (-not $procs) { 'panel not running'; exit 0 }

foreach ($p in $procs) {
    [Top]::SetWindowPos($p.MainWindowHandle, $HWND_NOTOPMOST, 0, 0, 0, 0, ($SWP_NOMOVE -bor $SWP_NOSIZE)) | Out-Null
}
"已取消置顶：$($procs.Count) 个窗口"
