using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Application.Abstractions;

/// <summary>更新检查结果。</summary>
/// <param name="Status">检查状态。</param>
/// <param name="Current">当前版本。</param>
/// <param name="Latest">远端最新版本；查询失败时为 <c>null</c>。</param>
/// <param name="Release">最新发布的完整信息；查询失败时为 <c>null</c>。</param>
/// <param name="Message">给用户看的一句话说明。</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    SemanticVersion Current,
    SemanticVersion? Latest,
    AppRelease? Release,
    string Message)
{
    /// <summary>是否存在可安装的更新。</summary>
    public bool HasUpdate => Status == UpdateCheckStatus.UpdateAvailable;
}

/// <summary>更新检查状态。</summary>
public enum UpdateCheckStatus
{
    /// <summary>已是最新版本。</summary>
    UpToDate = 0,

    /// <summary>发现新版本。</summary>
    UpdateAvailable = 1,

    /// <summary>查询失败（无网络、接口限流等）。</summary>
    Failed = 2,
}

/// <summary>更新检查与在线安装。</summary>
public interface IUpdateService
{
    /// <summary>当前运行版本。</summary>
    SemanticVersion CurrentVersion { get; }

    /// <summary>当前是否由安装程序安装（否则为便携版）。</summary>
    bool IsInstalled { get; }

    /// <summary>查询是否有新版本。</summary>
    Task<UpdateCheckResult> CheckAsync(bool includePrerelease = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// 下载安装包。
    /// </summary>
    /// <param name="release">目标发布。</param>
    /// <param name="progress">下载进度（0~1，总量未知时为 <c>null</c>）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>下载到本地的安装包路径。</returns>
    Task<string> DownloadInstallerAsync(
        AppRelease release,
        IProgress<double?>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 启动升级流程（接管安装、必要时重启）。
    /// </summary>
    /// <param name="installerPath">安装包本地路径。</param>
    /// <param name="relaunchAfterInstall">安装完成后是否自动重启应用。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>升级结果。</returns>
    /// <remarks>
    /// 安装包会检测「本程序是否仍在运行」，因此实现方必须先用独立进程等待本程序退出，
    /// 再由它执行静默安装。本方法返回 <see cref="UpdateInstallResult.RelaunchRequired"/>
    /// 时，调用方应立即关闭应用。
    /// </remarks>
    Task<UpdateInstallResult> RunInstallerAsync(
        string installerPath,
        bool relaunchAfterInstall = true,
        CancellationToken cancellationToken = default);
}

/// <summary>在线安装结果。</summary>
/// <param name="Success">安装流程是否已成功启动。</param>
/// <param name="Message">给用户看的说明。</param>
/// <param name="RelaunchRequired">
/// 是否需要在返回后立刻关闭应用：安装由独立进程接管，只有本程序退出后才会真正开始安装。
/// </param>
public sealed record UpdateInstallResult(bool Success, string Message, bool RelaunchRequired = false);
