using System.Net.Http;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Settings;
using DeskShelf.Infrastructure.Storage;
using DeskShelf.Infrastructure.Updates;
using DeskShelf.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 真实 GitHub Releases API 的集成测试。
/// </summary>
/// <remarks>
/// 默认跳过（会访问网络、消耗 API 配额）。启用方式：
/// <code>
/// $env:DESKSHELF_UPDATE_INTEGRATION = "1"
/// $env:GITHUB_TOKEN = (gh auth token)     # 可选，未认证配额只有 60 次/小时
/// dotnet test .\tests\DeskShelf.Tests\DeskShelf.Tests.csproj
/// </code>
/// 这层测试的价值：验证 JSON 契约（字段名、assets 结构）与真实响应一致 ——
/// 单元测试用的是手工构造的对象，无法发现 GitHub 改字段名这类问题。
/// </remarks>
public sealed class GitHubReleaseSourceIntegrationTests
{
    private const string Gate = "DESKSHELF_UPDATE_INTEGRATION";
    private const string SkipReason = "集成测试默认跳过：设置 DESKSHELF_UPDATE_INTEGRATION=1 后启用。";

    private static string? Token => Environment.GetEnvironmentVariable("GITHUB_TOKEN");

    private static GitHubReleaseSource CreateSource()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskShelf-Updater-Test");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return new GitHubReleaseSource(http, NullLogger<GitHubReleaseSource>.Instance);
    }

    [IntegrationFact(Gate, SkipReason)]
    public async Task GetLatest_ParsesRealReleaseWithInstallerAsset()
    {
        var source = CreateSource();

        var result = await source.GetLatestAsync(accessToken: Token);

        Assert.True(
            result.Success,
            $"查询失败：{result.Failure} / {result.Detail}。" +
            (result.Failure == ReleaseQueryFailure.RateLimited
                ? "（未认证配额 60 次/小时，可用 GITHUB_TOKEN 提升）"
                : string.Empty));

        var release = result.Release!;
        Assert.False(string.IsNullOrWhiteSpace(release.TagName));
        Assert.True(release.Version.Major >= 1, $"版本号解析异常：{release.Version}");
        Assert.Contains("github.com", release.PageUrl, StringComparison.OrdinalIgnoreCase);

        // 关键契约：必须能从真实 assets 里挑出安装包，否则在线安装会退化成手动下载
        var installer = release.FindInstaller();
        Assert.NotNull(installer);
        Assert.Contains("setup.exe", installer!.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(installer.SizeBytes > 0, "安装包大小应为正数");
    }

    [IntegrationFact(Gate, SkipReason)]
    public async Task UpdateService_AgainstRealApi_ReportsUpToDateForItsOwnVersion()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "DeskShelfInteg-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();

        var settings = new FakeSettingsStore();
        if (!string.IsNullOrWhiteSpace(Token))
        {
            await settings.SaveAsync(ShelfSettings.Default with { GitHubToken = Token });
        }

        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskShelf-Updater-Test");

        var service = new UpdateService(CreateSource(), settings, paths, http, NullLogger<UpdateService>.Instance);

        var result = await service.CheckAsync();

        Assert.NotEqual(UpdateCheckStatus.Failed, result.Status);
        Assert.True(result.Latest is not null, "应能读到远端最新版本");

        // 当前程序集版本与最新发布比较，两种结论都必须自洽
        if (result.Latest!.Value.IsNewerThan(service.CurrentVersion))
        {
            Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        }
        else
        {
            Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        }
    }

    /// <summary>
    /// 真实下载安装包：验证下载链路（重定向、进度回调、临时文件改名）与产物完整性。
    /// </summary>
    [IntegrationFact(Gate, SkipReason)]
    public async Task DownloadInstaller_FromRealRelease_ProducesExecutable()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "DeskShelfInteg-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();

        var settings = new FakeSettingsStore();
        if (!string.IsNullOrWhiteSpace(Token))
        {
            await settings.SaveAsync(ShelfSettings.Default with { GitHubToken = Token });
        }

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskShelf-Updater-Test");

        var service = new UpdateService(CreateSource(), settings, paths, http, NullLogger<UpdateService>.Instance);

        var query = await CreateSource().GetLatestAsync(accessToken: Token);
        Assert.True(query.Success, $"查询发布失败：{query.Failure} / {query.Detail}");

        var release = query.Release!;
        var asset = release.FindInstaller();
        Assert.NotNull(asset);

        var progressValues = new List<double?>();
        var path = await service.DownloadInstallerAsync(
            release,
            new Progress<double?>(value => progressValues.Add(value)));

        try
        {
            Assert.True(File.Exists(path), "安装包应已下载到本地");

            var info = new FileInfo(path);
            Assert.Equal(asset!.SizeBytes, info.Length);   // 字节级完整，不是被截断的半包
            Assert.True(info.Length > 1_000_000, $"安装包应有数 MB 量级，实际 {info.Length} 字节");

            // PE 头校验：确认下载到的是真正的可执行文件
            var header = new byte[2];
            await using (var stream = File.OpenRead(path))
            {
                _ = await stream.ReadAsync(header);
            }

            Assert.Equal((byte)'M', header[0]);
            Assert.Equal((byte)'Z', header[1]);

            // 文件名里带版本号，避免不同版本的残包互相覆盖
            Assert.Contains(release.Version.ToString(), path, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论
            }
        }
    }
}
