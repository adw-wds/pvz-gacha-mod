# 用 UI Automation 点击面板左侧导航（用于截图验证各个页面）
param([Parameter(Mandatory = $true)][string]$NavName)

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$root = [System.Windows.Automation.AutomationElement]::RootElement
$winCond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, '抽卡版PVZ 修改器')
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $winCond)
if (-not $win) { 'window not found'; exit 1 }

# 窗口可能被别的窗口盖住：先把它拉到前台
try {
    $hwnd = $win.Current.NativeWindowHandle
    Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
'@ -ErrorAction SilentlyContinue
    [W.U]::ShowWindow([IntPtr]$hwnd, 9) | Out-Null
    [W.U]::SetForegroundWindow([IntPtr]$hwnd) | Out-Null
    Start-Sleep -Milliseconds 600
} catch { }

$andCond = New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $NavName)),
    (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)))
$btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $andCond)
if (-not $btn) { "nav not found: $NavName"; exit 1 }

$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 900
"clicked: $NavName"
