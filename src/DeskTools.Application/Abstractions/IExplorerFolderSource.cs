namespace DeskTools.Application.Abstractions;

/// <summary>一个处于打开状态的资源管理器窗口。</summary>
/// <param name="LocationUrl">位置 URL（本地路径形如 <c>file:///C:/project/tools</c>）。</param>
/// <param name="DisplayName">显示名（通常是文件夹名）。</param>
public sealed record ExplorerWindowInfo(string LocationUrl, string DisplayName);

/// <summary>
/// 打开的「资源管理器」窗口查询。
/// </summary>
/// <remarks>
/// 存在的意义：用户希望「把正在看的这个目录存到面板上」。
/// 拖拽地址栏 / 标题栏在部分系统版本上无法触发（Explorer 侧行为，应用无法控制），
/// 因此提供一个不依赖拖拽的可靠通道：直接读取资源管理器进程的窗口位置。
/// </remarks>
public interface IExplorerFolderSource
{
    /// <summary>
    /// 获取当前打开的资源管理器窗口位置，<b>最近使用过的排在前面</b>。
    /// 没有打开任何资源管理器窗口时返回空集合。
    /// </summary>
    IReadOnlyList<ExplorerWindowInfo> GetOpenLocations();
}
