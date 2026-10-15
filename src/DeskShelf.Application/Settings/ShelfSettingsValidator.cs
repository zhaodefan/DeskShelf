namespace DeskShelf.Application.Settings;

/// <summary>
/// 设置校验与归一化：任何来源（手改 JSON、旧版本文件、将来可能的设置界面）
/// 得到的设置都必须先过这里，保证下游拿到的永远是合法值。
/// </summary>
public static class ShelfSettingsValidator
{
    /// <summary>面板最小宽度（DIP），低于此值卡片内容无法正常排布。</summary>
    public const double MinPanelWidth = 260d;

    /// <summary>面板最小高度（DIP）。</summary>
    public const double MinPanelHeight = 220d;

    /// <summary>面板最大宽度（DIP）。</summary>
    public const double MaxPanelWidth = 1600d;

    /// <summary>面板最大高度（DIP）。</summary>
    public const double MaxPanelHeight = 1600d;

    /// <summary>unit 数量上限的合法区间。</summary>
    public const int MinUnitLimit = 20;

    /// <summary>unit 数量上限的最大值。</summary>
    public const int MaxUnitLimit = 10_000;

    /// <summary>返回一个各项取值都合法的设置副本（非法项回退到默认值或收敛到边界）。</summary>
    public static ShelfSettings Normalize(ShelfSettings? settings)
    {
        if (settings is null)
        {
            return ShelfSettings.Default;
        }

        var placement = settings.Placement ?? WindowPlacement.Default;
        var hotkey = settings.Hotkey is { IsValid: true } ? settings.Hotkey : HotkeyGesture.Default;
        var theme = Enum.IsDefined(settings.Theme) ? settings.Theme : AppTheme.System;

        return settings with
        {
            SchemaVersion = settings.SchemaVersion <= 0 ? 1 : settings.SchemaVersion,
            Hotkey = hotkey,
            Theme = theme,
            Placement = placement with
            {
                Width = Clamp(placement.Width, MinPanelWidth, MaxPanelWidth, WindowPlacement.Default.Width),
                Height = Clamp(placement.Height, MinPanelHeight, MaxPanelHeight, WindowPlacement.Default.Height),
            },
            MaxUnits = Math.Clamp(settings.MaxUnits, MinUnitLimit, MaxUnitLimit),
        };
    }

    /// <summary>判断窗口矩形是否仍与某个显示器可见区域相交（显示器拔除后用于回退定位）。</summary>
    public static bool IsPlacementVisible(WindowPlacement placement, IReadOnlyList<ScreenRect> screens, double minVisible = 48d)
    {
        ArgumentNullException.ThrowIfNull(placement);
        ArgumentNullException.ThrowIfNull(screens);

        if (placement.Left is not { } left || placement.Top is not { } top)
        {
            return false;
        }

        var window = new ScreenRect(left, top, placement.Width, placement.Height);
        foreach (var screen in screens)
        {
            var intersectWidth = Math.Min(window.Right, screen.Right) - Math.Max(window.Left, screen.Left);
            var intersectHeight = Math.Min(window.Bottom, screen.Bottom) - Math.Max(window.Top, screen.Top);
            if (intersectWidth >= minVisible && intersectHeight >= minVisible)
            {
                return true;
            }
        }

        return false;
    }

    private static double Clamp(double value, double min, double max, double fallback)
        => double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}

/// <summary>屏幕工作区矩形（DIP），由基础设施层提供，避免领域/应用层依赖 Win32。</summary>
/// <param name="Left">左边缘。</param>
/// <param name="Top">上边缘。</param>
/// <param name="Width">宽度。</param>
/// <param name="Height">高度。</param>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    /// <summary>右边缘。</summary>
    public double Right => Left + Width;

    /// <summary>下边缘。</summary>
    public double Bottom => Top + Height;
}
