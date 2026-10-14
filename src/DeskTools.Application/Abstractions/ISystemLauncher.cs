using DeskTools.Domain.Units;

namespace DeskTools.Application.Abstractions;

/// <summary>
/// 打开 / 定位 unit 对应的系统资源。双击 unit 与右键菜单的「打开」都走这里，
/// 通过 <see cref="UnitOpenBehavior"/> 分派，避免 UI 按具体类型写分支。
/// </summary>
public interface ISystemLauncher
{
    /// <summary>
    /// 执行 unit 的默认打开动作（文本 unit 无动作，返回 <c>false</c>）。
    /// </summary>
    /// <returns>是否成功发起打开动作。</returns>
    Task<bool> OpenAsync(ShelfUnit unit, CancellationToken cancellationToken = default);

    /// <summary>在资源管理器中定位该 unit 对应的文件/文件夹。</summary>
    Task<bool> RevealInExplorerAsync(ShelfUnit unit, CancellationToken cancellationToken = default);

    /// <summary>打开系统文件（例如设置文件所在目录），用于「设置」入口的降级动作。</summary>
    Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default);
}
