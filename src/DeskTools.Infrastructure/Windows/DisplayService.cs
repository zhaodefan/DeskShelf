using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DeskTools.Application.Abstractions;
using DeskTools.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Windows;

/// <summary>
/// 显示器查询与「面板默认落在鼠标所在屏右下角」的定位计算。
/// 所有 Win32 坐标（物理像素）在此转换为 WPF 使用的 DIP 坐标。
/// </summary>
public sealed class DisplayService : IDisplayService
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    private readonly ILogger<DisplayService> _logger;

    /// <summary>创建显示器服务。</summary>
    public DisplayService(ILogger<DisplayService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<ScreenRect> GetWorkingAreas()
    {
        var areas = new List<ScreenRect>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                if (TryGetWorkingArea(monitor) is { } rect)
                {
                    areas.Add(rect);
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举显示器失败，回退到主屏。");
        }

        if (areas.Count == 0)
        {
            areas.Add(FallbackPrimaryArea());
        }

        return areas;
    }

    /// <inheritdoc />
    public ScreenRect GetWorkingAreaFromCursor()
    {
        try
        {
            return GetCursorPos(out var point)
                ? GetWorkingAreaFromPoint(point.X, point.Y) ?? FallbackPrimaryArea()
                : FallbackPrimaryArea();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "获取鼠标所在显示器失败，回退到主屏。");
            return FallbackPrimaryArea();
        }
    }

    /// <inheritdoc />
    public string? GetMonitorDeviceNameFromCursor()
    {
        try
        {
            if (!GetCursorPos(out var point))
            {
                return null;
            }

            var monitor = MonitorFromPoint(point, MonitorDefaultToNearest);
            return monitor == IntPtr.Zero ? null : ReadDeviceName(monitor);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取显示器设备名失败。");
            return null;
        }
    }

    /// <summary>
    /// 计算面板首次出现的位置：鼠标所在屏工作区的右下角，留出 16 DIP 边距。
    /// </summary>
    /// <param name="size">面板尺寸（DIP）。</param>
    /// <param name="margin">与工作区边缘的间距（DIP）。</param>
    public Point CalculateDefaultPosition(System.Windows.Size size, double margin = 16d)
    {
        var area = GetWorkingAreaFromCursor();
        return new Point(
            area.Right - size.Width - margin,
            area.Bottom - size.Height - margin);
    }

    private ScreenRect? GetWorkingAreaFromPoint(int x, int y)
    {
        var monitor = MonitorFromPoint(new NativePoint(x, y), MonitorDefaultToNearest);
        return monitor == IntPtr.Zero ? null : TryGetWorkingArea(monitor);
    }

    private ScreenRect? TryGetWorkingArea(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return null;
        }

        // 物理像素 → DIP：使用该显示器的缩放比例
        var scale = GetScaleFactor(monitor);
        return ToDip(info.Work, scale);
    }

    private static double GetScaleFactor(IntPtr monitor)
    {
        var scale = 1d;
        try
        {
            if (GetDpiForMonitor(monitor, MonitorDpiType.EffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
            {
                scale = dpiX / 96d;
            }
        }
        catch (DllNotFoundException)
        {
            // 极端精简的系统缺少 shcore.dll：退回 100%
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 8.1 以下没有 GetDpiForMonitor：退回 100%
        }

        return scale <= 0 ? 1d : scale;
    }

    private static ScreenRect ToDip(NativeRect physical, double scale)
        => new(physical.Left / scale, physical.Top / scale, physical.Width / scale, physical.Height / scale);

    private static ScreenRect FallbackPrimaryArea()
    {
        var area = SystemParameters.WorkArea;
        return new ScreenRect(area.Left, area.Top, area.Width, area.Height);
    }

    private static string? ReadDeviceName(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) ? info.DeviceName : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;

        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private enum MonitorDpiType
    {
        EffectiveDpi = 0,
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// 依据「记住的位置 + 当前显示器布局」计算面板最终位置。
    /// 显示器被拔除 / 分辨率变化导致记忆位置落在可见区域之外时，回退到默认右下角。
    /// </summary>
    public static Point ResolvePosition(WindowPlacement placement, System.Windows.Size size, IDisplayService display)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(display);

        var areas = display.GetWorkingAreas();
        if (placement.Left is { } left && placement.Top is { } top &&
            ShelfSettingsValidator.IsPlacementVisible(placement, areas))
        {
            return new Point(left, top);
        }

        var cursorArea = display.GetWorkingAreaFromCursor();
        return new Point(
            cursorArea.Right - size.Width - 16d,
            cursorArea.Bottom - size.Height - 16d);
    }
}
