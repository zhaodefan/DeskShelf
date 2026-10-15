# DeskShelf 界面自动化辅助（供开发期验证使用，不参与产品构建）
#
# 用途：在没有人工操作的情况下验证窗口/控件行为。提供：
#   - Find-Window            按进程名 + 标题找窗口
#   - Get-WindowRect         读取窗口物理坐标
#   - Invoke-UiaClick        用 UI Automation 模式操作控件（不依赖坐标，最稳）
#   - Send-Keys              发送按键
#   - Save-WindowShot        截取窗口位图
#
# 使用方式（在仓库根目录）：
#   . .\tools\UiProbe.ps1
#   $w = Find-Window -ProcessName DeskShelf -Title 'DeskShelf 设置'
#   Invoke-UiaClick -Window $w -Name '浅色' -ControlType RadioButton

Set-StrictMode -Version Latest

if (-not ('DeskShelfProbe' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, UIAutomationClient, UIAutomationTypes -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeskShelfProbe
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<IntPtr> WindowsOf(uint pid)
    {
        var result = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            uint owner;
            GetWindowThreadProcessId(hwnd, out owner);
            if (owner == pid && IsWindowVisible(hwnd)) result.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowTextW(hwnd, buffer, 512);
        return buffer.ToString();
    }

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        System.Threading.Thread.Sleep(180);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(70);
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
    }

    public static string Capture(IntPtr hwnd, string path)
    {
        RECT rect;
        GetWindowRect(hwnd, out rect);
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        using (var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        return string.Format("{0}x{1} @ ({2},{3})", width, height, rect.Left, rect.Top);
    }
}
'@
}

function Find-Window {
    param(
        [Parameter(Mandatory)][string]$ProcessName,
        [string]$Title
    )

    $process = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
    if (-not $process) { return $null }

    foreach ($hwnd in [DeskShelfProbe]::WindowsOf([uint32]$process.Id)) {
        $text = [DeskShelfProbe]::TitleOf($hwnd)
        if (-not $Title -or $text -eq $Title) { return $hwnd }
    }

    return $null
}

function Get-WindowRect {
    param([Parameter(Mandatory)][IntPtr]$Window)

    $rect = New-Object DeskShelfProbe+RECT
    [void][DeskShelfProbe]::GetWindowRect($Window, [ref]$rect)
    return $rect
}

function Get-UiaElement {
    param(
        [Parameter(Mandatory)][IntPtr]$Window,
        [string]$Name,
        [string]$ControlType
    )

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
    $conditions = New-Object System.Collections.Generic.List[System.Windows.Automation.Condition]
    if ($Name) {
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)))
    }
    if ($ControlType) {
        $type = [System.Windows.Automation.ControlType]::$ControlType
        $conditions.Add((New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
    }

    $condition = if ($conditions.Count -eq 0) {
        [System.Windows.Automation.Condition]::TrueCondition
    } elseif ($conditions.Count -eq 1) {
        $conditions[0]
    } else {
        New-Object System.Windows.Automation.AndCondition($conditions.ToArray())
    }

    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

# 用 UI Automation 模式操作控件：优先 SelectionItem（单选）、Toggle（复选）、Invoke（按钮）
function Invoke-UiaClick {
    param(
        [Parameter(Mandatory)][IntPtr]$Window,
        [Parameter(Mandatory)][string]$Name,
        [string]$ControlType
    )

    $element = Get-UiaElement -Window $Window -Name $Name -ControlType $ControlType
    if (-not $element) { throw "未找到控件：$Name" }

    # 注意：每种模式只调用它自己的方法，不能混用（TogglePattern 没有 Select）
    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$null)) {
        $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        return $true
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$null)) {
        $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
        return $true
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$null)) {
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        return $true
    }

    # 没有可用模式时退化为坐标点击
    $rect = $element.Current.BoundingRectangle
    [DeskShelfProbe]::Click([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
    return $true
}

function Get-UiaState {
    param(
        [Parameter(Mandatory)][IntPtr]$Window,
        [Parameter(Mandatory)][string]$Name
    )

    $element = Get-UiaElement -Window $Window -Name $Name
    if (-not $element) { return 'missing' }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$null)) {
        return "selected=$($element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected)"
    }

    if ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$null)) {
        return "toggle=$($element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState)"
    }

    return 'present'
}

function Send-Keys {
    param([Parameter(Mandatory)][string]$Keys)

    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
}

function Save-WindowShot {
    param(
        [Parameter(Mandatory)][IntPtr]$Window,
        [Parameter(Mandatory)][string]$Path
    )

    $directory = Split-Path -Parent $Path
    if ($directory -and -not (Test-Path $directory)) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    return [DeskShelfProbe]::Capture($Window, $Path)
}

function Get-DeskShelfLog {
    param([int]$Tail = 20)

    $log = Get-ChildItem "$env:APPDATA\DeskShelf\logs" -Filter '*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { return @('（无日志文件）') }
    return Get-Content $log.FullName -Tail $Tail
}
