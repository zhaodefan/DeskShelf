namespace DeskShelf.Application.Settings;

/// <summary>面板窗口的位置与尺寸（屏幕坐标，单位 DIP；首次运行为 <c>null</c> 时按右下角默认定位）。</summary>
public sealed record WindowPlacement
{
    /// <summary>左边缘（虚拟屏幕坐标）。</summary>
    public double? Left { get; init; }

    /// <summary>上边缘（虚拟屏幕坐标）。</summary>
    public double? Top { get; init; }

    /// <summary>宽度（DIP，默认 360）。</summary>
    public double Width { get; init; } = 360d;

    /// <summary>高度（DIP，默认 480）。</summary>
    public double Height { get; init; } = 480d;

    /// <summary>所在显示器的设备名（例如 <c>\\.\DISPLAY1</c>），显示器变化时用于回退到主屏。</summary>
    public string? MonitorDeviceName { get; init; }

    /// <summary>默认位置与尺寸。</summary>
    public static WindowPlacement Default { get; } = new();
}
