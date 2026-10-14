namespace DeskTools.Domain.Units;

/// <summary>
/// unit 的能力声明。UI 依据能力决定按钮的显示/禁用，<b>不得</b>按具体类型写 if-else 分支。
/// </summary>
/// <param name="CanCopyToClipboard">能否被「选中 + Ctrl+C」写入剪贴板。文件夹 unit 为 <c>false</c>。</param>
/// <param name="CanEditText">能否在面板内行内编辑文本。</param>
/// <param name="OpenBehavior">双击/「打开」时的动作类型。</param>
public readonly record struct UnitCapabilities(
    bool CanCopyToClipboard,
    bool CanEditText,
    UnitOpenBehavior OpenBehavior)
{
    /// <summary>是否具备「在资源管理器中显示」的附加动作。</summary>
    public bool CanReveal => OpenBehavior is UnitOpenBehavior.OpenFolder or UnitOpenBehavior.OpenImage;

    /// <summary>右键菜单是否为空（没有任何可用动作）。</summary>
    public bool HasAnyAction => CanCopyToClipboard || CanEditText || OpenBehavior != UnitOpenBehavior.None;
}

/// <summary>双击 unit 时的动作类型。</summary>
public enum UnitOpenBehavior
{
    /// <summary>无打开动作（例如文本 unit）。</summary>
    None = 0,

    /// <summary>用系统默认看图程序打开图片文件。</summary>
    OpenImage = 1,

    /// <summary>用资源管理器打开文件夹路径。</summary>
    OpenFolder = 2,
}
