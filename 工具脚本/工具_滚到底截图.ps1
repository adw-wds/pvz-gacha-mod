# 让面板滚到最底并截图（系统组在页面下方）
param([string]$Out = "$env:TEMP\panel_bottom.png")
$ErrorActionPreference = 'Stop'

$src = @'
using System;
using System.Runtime.InteropServices;
public static class SC {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr c, string cls, string win);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, int d, UIntPtr e);
}
'@
Add-Type -TypeDefinition $src -ErrorAction SilentlyContinue

$PANEL = ([char]0x62BD) + ([char]0x5361) + ([char]0x7248) + ([char]0x4FEE) + ([char]0x6539) + ([char]0x5668)
$p = Get-Process $PANEL -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { 'panel not found'; exit 1 }
$h = $p.MainWindowHandle

[SC]::ShowWindow($h, 9) | Out-Null
[SC]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 800

# 面板中央 = 内容区，在那儿滚轮往下滚
[SC]::SetCursorPos(700, 520) | Out-Null
Start-Sleep -Milliseconds 200
$MOUSEEVENTF_WHEEL = 0x0800
foreach ($i in 1..40) {
    [SC]::mouse_event($MOUSEEVENTF_WHEEL, 0, 0, -120, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 25
}
Start-Sleep -Milliseconds 700

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = New-Object System.Drawing.Bitmap 1,1
# 用窗口区域截图
$r = New-Object SC -ErrorAction SilentlyContinue
