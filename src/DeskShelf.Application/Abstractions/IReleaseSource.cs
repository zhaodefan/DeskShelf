using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Application.Abstractions;

/// <summary>Release 中的一个可下载文件。</summary>
/// <param name="Name">文件名（例如 <c>DeskShelf-1.2.0-setup.exe</c>）。</param>
/// <param name="DownloadUrl">下载地址。</param>
/// <param name="SizeBytes">字节数；服务端未提供时为 0。</param>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long SizeBytes);

/// <summary>一次发布（对应 GitHub 上的一个 Release）。</summary>
/// <param name="Version">版本号。</param>
/// <param name="TagName">原始标签名（例如 <c>v1.2.0</c>）。</param>
/// <param name="IsPrerelease">是否为预发布。</param>
/// <param name="PageUrl">发布页地址（无法自动更新时引导用户手动下载）。</param>
/// <param name="Assets">附件列表。</param>
/// <param name="Notes">发布说明（Markdown）。</param>
public sealed record AppRelease(
    SemanticVersion Version,
    string TagName,
    bool IsPrerelease,
    string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets,
    string? Notes)
{
    /// <summary>
    /// 找出可用于静默升级的安装包。
    /// </summary>
    /// <returns>安装包附件；没有则返回 <c>null</c>（此时应引导用户到发布页手动下载）。</returns>
    public ReleaseAsset? FindInstaller()
        => Assets.FirstOrDefault(a =>
            a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase) ||
            a.Name.EndsWith("_setup.exe", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 发布查询失败的原因。
/// </summary>
/// <remarks>
/// 区分「限流」与「其它网络错误」的意义：未认证的 GitHub API 只有 60 次/小时/IP，
/// 撞上限流时必须告诉用户「什么时候可以再试」，而不是笼统地说「网络错误」。
/// </remarks>
public enum ReleaseQueryFailure
{
    /// <summary>未失败。</summary>
    None = 0,

    /// <summary>触发 API 限流。</summary>
    RateLimited = 1,

    /// <summary>网络不可达、超时或返回了无法解析的内容。</summary>
    Network = 2,
}

/// <summary>带失败原因的发布查询结果。</summary>
/// <param name="Release">查询到的发布；失败时为 <c>null</c>。</param>
/// <param name="Failure">失败原因。</param>
/// <param name="RetryAfter">限流时的建议重试时间（本地时间）。</param>
/// <param name="Detail">技术细节，用于日志。</param>
public sealed record ReleaseQueryResult(
    AppRelease? Release,
    ReleaseQueryFailure Failure = ReleaseQueryFailure.None,
    DateTimeOffset? RetryAfter = null,
    string? Detail = null)
{
    /// <summary>是否查询成功。</summary>
    public bool Success => Failure == ReleaseQueryFailure.None && Release is not null;

    /// <summary>构造成功结果。</summary>
    public static ReleaseQueryResult Ok(AppRelease release) => new(release);
}

/// <summary>
/// 发布信息源（当前实现基于 GitHub Releases API）。
/// </summary>
public interface IReleaseSource
{
    /// <summary>
    /// 查询最新发布。
    /// </summary>
    /// <param name="includePrerelease">是否把预发布版也算作「最新」。</param>
    /// <param name="accessToken">
    /// 可选的 GitHub 访问令牌：未认证时限流只有 60 次/小时/IP，提供令牌可提升到 5000 次/小时。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>查询结果（含失败原因）。</returns>
    Task<ReleaseQueryResult> GetLatestAsync(
        bool includePrerelease = false,
        string? accessToken = null,
        CancellationToken cancellationToken = default);
}
