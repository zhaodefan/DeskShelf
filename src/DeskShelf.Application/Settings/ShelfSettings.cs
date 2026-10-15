namespace DeskShelf.Application.Settings;

/// <summary>应用设置（持久化于 <c>%APPDATA%\DeskShelf\settings.json</c>）。</summary>
public sealed record ShelfSettings
{
    /// <summary>设置架构版本，便于将来迁移。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>呼出/关闭面板的全局热键（默认 Alt+X）。</summary>
    public HotkeyGesture Hotkey { get; init; } = HotkeyGesture.Default;

    /// <summary>面板窗口位置与尺寸。</summary>
    public WindowPlacement Placement { get; init; } = WindowPlacement.Default;

    /// <summary>主题偏好（默认跟随系统）。</summary>
    public AppTheme Theme { get; init; } = AppTheme.System;

    /// <summary>开机自启（默认关闭，已确认）。</summary>
    public bool AutoStart { get; init; }

    /// <summary>失焦时是否自动隐藏面板（默认关闭，已确认）。</summary>
    public bool HideOnDeactivate { get; init; }

    /// <summary>粘贴/拖入的图片是否在面板中显示缩略图（关闭后仅显示占位信息，用于极低配机器）。</summary>
    public bool ShowImageThumbnails { get; init; } = true;

    /// <summary>unit 数量上限，超出时拒绝新增并提示。</summary>
    public int MaxUnits { get; init; } = 500;

    /// <summary>
    /// 可选的 GitHub 访问令牌，仅用于「检查更新」时提升 API 配额。
    /// </summary>
    /// <remarks>
    /// 未认证的 GitHub API 限流为 60 次/小时/IP，在 NAT 或公司网络下很容易被用尽；
    /// 提供只读令牌可提升到 5000 次/小时。留空即匿名访问（默认）。
    /// 令牌只保存在本机 <c>settings.json</c>，除 GitHub API 外不会发往任何地方。
    /// </remarks>
    public string? GitHubToken { get; init; }

    /// <summary>默认设置。</summary>
    public static ShelfSettings Default { get; } = new();
}
