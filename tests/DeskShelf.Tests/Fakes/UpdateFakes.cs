using DeskShelf.Application.Abstractions;
using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Tests.Fakes;

/// <summary>可控的发布信息源（用于更新检查测试）。</summary>
public sealed class FakeReleaseSource : IReleaseSource
{
    /// <summary>下一次查询返回的发布；为 <c>null</c> 表示查询失败。</summary>
    public AppRelease? Latest { get; set; }

    /// <summary>下一次查询的失败类型（用于验证限流等场景的提示文案）。</summary>
    public ReleaseQueryFailure Failure { get; set; } = ReleaseQueryFailure.None;

    /// <summary>限流时的建议重试时间。</summary>
    public DateTimeOffset? RetryAfter { get; set; }

    /// <summary>查询次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>最后一次查询是否包含预发布。</summary>
    public bool LastIncludePrerelease { get; private set; }

    /// <summary>最后一次查询使用的访问令牌。</summary>
    public string? LastAccessToken { get; private set; }

    /// <inheritdoc />
    public Task<ReleaseQueryResult> GetLatestAsync(
        bool includePrerelease = false,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        LastIncludePrerelease = includePrerelease;
        LastAccessToken = accessToken;

        return Task.FromResult(Failure != ReleaseQueryFailure.None
            ? new ReleaseQueryResult(null, Failure, RetryAfter, "测试注入的失败")
            : Latest is null
                ? new ReleaseQueryResult(null, ReleaseQueryFailure.Network, null, "测试：无发布")
                : ReleaseQueryResult.Ok(Latest));
    }

    /// <summary>构造一个带安装包的发布。</summary>
    public static AppRelease ReleaseWithInstaller(string version, string? pageUrl = null)
        => new(
            SemanticVersion.TryParse(version, out var parsed) ? parsed : new SemanticVersion(0, 0, 0),
            "v" + version,
            false,
            pageUrl ?? $"https://example.invalid/releases/tag/v{version}",
            new[]
            {
                new ReleaseAsset($"DeskShelf-{version}-setup.exe", $"https://example.invalid/DeskShelf-{version}-setup.exe", 1024),
                new ReleaseAsset($"DeskShelf-{version}-portable-win-x64.zip", $"https://example.invalid/portable-{version}.zip", 2048),
            },
            "测试发布说明");
}

/// <summary>可控的更新服务假实现。</summary>
public sealed class FakeUpdateService : IUpdateService
{
    /// <summary>当前版本（默认 1.0.0）。</summary>
    public SemanticVersion CurrentVersion { get; set; } = new(1, 0, 0);

    /// <summary>是否为安装版。</summary>
    public bool IsInstalled { get; set; } = true;

    /// <summary>检查结果；为 <c>null</c> 时按「已是最新」返回。</summary>
    public UpdateCheckResult? CheckResult { get; set; }

    /// <summary>下载是否失败。</summary>
    public bool FailDownload { get; set; }

    /// <summary>安装程序运行结果。</summary>
    public UpdateInstallResult InstallResult { get; set; } = new(true, "升级完成。");

    /// <summary>检查次数。</summary>
    public int CheckCount { get; private set; }

    /// <summary>下载次数。</summary>
    public int DownloadCount { get; private set; }

    /// <summary>运行安装程序次数。</summary>
    public int InstallCount { get; private set; }

    /// <summary>模拟的下载文件路径。</summary>
    public string DownloadPath { get; set; } = Path.Combine(Path.GetTempPath(), "DeskShelf-fake-setup.exe");

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckAsync(bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        CheckCount++;
        return Task.FromResult(CheckResult ?? new UpdateCheckResult(
            UpdateCheckStatus.UpToDate, CurrentVersion, CurrentVersion, null, $"已是最新版本（{CurrentVersion}）。"));
    }

    /// <inheritdoc />
    public Task<string> DownloadInstallerAsync(
        AppRelease release,
        IProgress<double?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        DownloadCount++;
        if (FailDownload)
        {
            throw new InvalidOperationException("模拟下载失败");
        }

        progress?.Report(0.5d);
        progress?.Report(1d);
        return Task.FromResult(DownloadPath);
    }

    /// <inheritdoc />
    public Task<UpdateInstallResult> RunInstallerAsync(
        string installerPath,
        bool relaunchAfterInstall = true,
        CancellationToken cancellationToken = default)
    {
        InstallCount++;
        return Task.FromResult(InstallResult);
    }
}

/// <summary>同步执行的 UI 调度器（测试环境没有 Dispatcher）。</summary>
public sealed class FakeUiDispatcher : IUiDispatcher
{
    /// <inheritdoc />
    public bool IsOnUiThread => true;

    /// <inheritdoc />
    public void Post(Action action) => action();

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
