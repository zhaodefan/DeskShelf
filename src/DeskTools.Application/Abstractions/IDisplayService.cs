using DeskTools.Application.Settings;

namespace DeskTools.Application.Abstractions;

/// <summary>显示器信息查询（工作区矩形、DPI 缩放、鼠标所在屏）。</summary>
public interface IDisplayService
{
    /// <summary>全部显示器的工作区矩形（已扣除任务栏，单位 DIP）。顺序与系统一致，主屏在前。</summary>
    IReadOnlyList<ScreenRect> GetWorkingAreas();

    /// <summary>鼠标当前所在显示器的工作区矩形；查询失败时回退到主屏。</summary>
    ScreenRect GetWorkingAreaFromCursor();

    /// <summary>鼠标当前所在显示器的设备名（例如 <c>\\.\DISPLAY1</c>），用于记忆面板归属屏。</summary>
    string? GetMonitorDeviceNameFromCursor();
}
