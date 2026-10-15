using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DeskShelf.Application.Abstractions;
using DeskShelf.Domain.ValueObjects;
using DeskShelf.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Infrastructure.Updates;

/// <summary>
/// 基于 GitHub Releases API 的发布信息源。
/// </summary>
/// <remarks>
/// 只依赖公开的只读接口，不需要 token；未认证请求有速率限制（约 60 次/小时/IP），
/// 因此失败时必须优雅降级为「引导用户到发布页」，而不是报错卡住界面。
/// </remarks>
public sealed class GitHubReleaseSource : IReleaseSource
{
    /// <summary>默认仓库（可在设置中覆盖，便于 fork 后自建更新源）。</summary>
    public const string DefaultRepository = "zhaodefan/DeskShelf";

    private readonly HttpClient _http;
    private readonly ILogger<GitHubReleaseSource> _logger;

    /// <summary>创建发布信息源。</summary>
    public GitHubReleaseSource(HttpClient http, ILogger<GitHubReleaseSource> logger)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ReleaseQueryResult> GetLatestAsync(
        bool includePrerelease = false,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        // 预发布版走 /releases 列表（第一个即最新），正式版走 /releases/latest
        var url = includePrerelease
            ? $"https://api.github.com/repos/{DefaultRepository}/releases?per_page=1"
            : $"https://api.github.com/repos/{DefaultRepository}/releases/latest";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Trim());
            }

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ClassifyFailure(response, url);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);

            var root = includePrerelease ? FirstArrayElement(document.RootElement) : document.RootElement;
            if (root is null)
            {
                return new ReleaseQueryResult(null, ReleaseQueryFailure.Network, null, "仓库还没有任何 Release。");
            }

            var release = ParseRelease(root.Value);
            return release is null
                ? new ReleaseQueryResult(null, ReleaseQueryFailure.Network, null, "无法解析 Release 内容。")
                : ReleaseQueryResult.Ok(release);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "查询最新版本失败（网络或接口异常）。");
            return new ReleaseQueryResult(null, ReleaseQueryFailure.Network, null, ex.Message);
        }
    }

    /// <summary>
    /// 把 HTTP 失败分类。限流（403/429）单独识别，并算出可重试时间。
    /// </summary>
    private ReleaseQueryResult ClassifyFailure(HttpResponseMessage response, string url)
    {
        var status = (int)response.StatusCode;
        var remaining = response.Headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            ? remainingValues.FirstOrDefault()
            : null;

        var isRateLimited = (status is 403 or 429) && remaining == "0";

        if (isRateLimited)
        {
            var reset = response.Headers.TryGetValues("X-RateLimit-Reset", out var resetValues) &&
                        long.TryParse(resetValues.FirstOrDefault(), out var unixSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
                : (DateTimeOffset?)null;

            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                reset = DateTimeOffset.Now.Add(delta);
            }

            _logger.LogWarning(
                "GitHub API 触发限流（HTTP {Status}），配额重置时间 {Reset:yyyy-MM-dd HH:mm:ss}。{Url}",
                status, reset, url);

            return new ReleaseQueryResult(
                null,
                ReleaseQueryFailure.RateLimited,
                reset,
                $"HTTP {status}，X-RateLimit-Remaining=0");
        }

        _logger.LogWarning("查询最新版本失败：HTTP {Status}（{Url}）", status, url);
        return new ReleaseQueryResult(null, ReleaseQueryFailure.Network, null, $"HTTP {status}");
    }

    private AppRelease? ParseRelease(JsonElement release)
    {
        var tag = release.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
        if (!SemanticVersion.TryParse(tag, out var version))
        {
            _logger.LogWarning("无法解析 Release 标签为版本号：{Tag}", tag);
            return null;
        }

        var pageUrl = release.TryGetProperty("html_url", out var urlElement)
            ? urlElement.GetString() ?? string.Empty
            : string.Empty;

        var isPrerelease = release.TryGetProperty("prerelease", out var preElement) && preElement.ValueKind == JsonValueKind.True;

        var notes = release.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;

        var assets = new List<ReleaseAsset>();
        if (release.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                var downloadUrl = asset.TryGetProperty("browser_download_url", out var downloadElement)
                    ? downloadElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(downloadUrl))
                {
                    continue;
                }

                var size = asset.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value) ? value : 0L;
                assets.Add(new ReleaseAsset(name, downloadUrl, size));
            }
        }

        return new AppRelease(version, tag ?? version.ToString(), isPrerelease, pageUrl, assets, notes);
    }

    private static JsonElement? FirstArrayElement(JsonElement root)
        => root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : null;
}

/// <summary>
/// 更新服务：检查版本、下载安装包、静默升级。
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>安装包下载目录（放在数据目录下，便于统一清理）。</summary>
    private const string DownloadFolderName = "updates";

    private readonly IReleaseSource _releases;
    private readonly ISettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly ILogger<UpdateService> _logger;

    /// <summary>创建更新服务。</summary>
    public UpdateService(IReleaseSource releases, ISettingsStore settings, AppPaths paths, HttpClient http, ILogger<UpdateService> logger)
    {
        _releases = releases ?? throw new ArgumentNullException(nameof(releases));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        CurrentVersion = ReadCurrentVersion();
        IsInstalled = DetectInstalled();
    }

    /// <inheritdoc />
    public SemanticVersion CurrentVersion { get; }

    /// <inheritdoc />
    public bool IsInstalled { get; }

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckAsync(bool includePrerelease = false, CancellationToken cancellationToken = default)
    {
        var query = await _releases
            .GetLatestAsync(includePrerelease, _settings.Current.GitHubToken, cancellationToken)
            .ConfigureAwait(false);

        if (query.Failure == ReleaseQueryFailure.RateLimited)
        {
            var hint = query.RetryAfter is { } reset
                ? $"（配额将在 {reset:HH:mm} 左右重置）"
                : string.Empty;

            _logger.LogWarning("检查更新被限流：{Detail}", query.Detail);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                CurrentVersion,
                null,
                null,
                $"GitHub 接口调用次数已用尽{hint}。可在设置里填入 GitHub 访问令牌提升配额，或稍后重试。");
        }

        if (!query.Success)
        {
            _logger.LogWarning("检查更新失败：{Detail}", query.Detail);
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                CurrentVersion,
                null,
                null,
                "无法获取版本信息（网络不可达或接口异常），可稍后重试。");
        }

        var release = query.Release!;

        if (release.Version.IsNewerThan(CurrentVersion))
        {
            var hasInstaller = release.FindInstaller() is not null && IsInstalled;
            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                CurrentVersion,
                release.Version,
                release,
                hasInstaller
                    ? $"发现新版本 {release.Version}。"
                    : $"发现新版本 {release.Version}。");
        }

        return new UpdateCheckResult(
            UpdateCheckStatus.UpToDate,
            CurrentVersion,
            release.Version,
            release,
            $"已是最新版本（{CurrentVersion}）。");
    }

    /// <inheritdoc />
    public async Task<string> DownloadInstallerAsync(
        AppRelease release,
        IProgress<double?>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);

        var asset = release.FindInstaller()
            ?? throw new InvalidOperationException($"发布 {release.TagName} 没有提供安装包。");

        var directory = Path.Combine(_paths.Root, DownloadFolderName);
        Directory.CreateDirectory(directory);

        // 文件名里带版本号，避免不同版本的残包互相覆盖
        var target = Path.Combine(directory, $"{release.Version}-{asset.Name}");
        var temp = target + ".part";

        _logger.LogInformation("开始下载安装包 {Name}（{Size} 字节）。", asset.Name, asset.SizeBytes);

        using (var response = await _http
                   .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? (asset.SizeBytes > 0 ? asset.SizeBytes : null);

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var destination = File.Create(temp))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    progress?.Report(total is > 0 ? (double)received / total.Value : null);
                }
            }
        }

        // 下载完成后再改名为最终文件：中断时不会留下一个「看起来完整」的安装包
        if (File.Exists(target))
        {
            File.Delete(target);
        }

        File.Move(temp, target);
        _logger.LogInformation("安装包下载完成：{Path}", target);
        progress?.Report(1d);
        return target;
    }

    /// <inheritdoc />
    public async Task<UpdateInstallResult> RunInstallerAsync(
        string installerPath,
        bool relaunchAfterInstall = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return new UpdateInstallResult(false, "安装包不存在，请重新下载。");
        }

        // /SILENT 而不是 /VERYSILENT：保留进度窗口，用户能看到升级在进行中；
        // /SUPPRESSMSGBOXES 避免任何弹窗阻塞；/NORESTART 防止误触发重启。
        var arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL";

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = arguments,
                // 安装程序需要提升/独立运行环境，交给 Shell 启动
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Environment.CurrentDirectory,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                return new UpdateInstallResult(false, "无法启动安装程序。");
            }

            _logger.LogInformation("已启动安装程序（PID {Pid}），等待其结束。", process.Id);

            // 不能用 using + WaitForExit 的同步重载：需要带超时的等待，避免安装器卡住导致进程堆积
            var exited = await WaitForExitAsync(process, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
            if (!exited)
            {
                return new UpdateInstallResult(false, "安装程序在 5 分钟内未结束，请手动完成安装。");
            }

            if (process.ExitCode != 0)
            {
                _logger.LogWarning("安装程序退出码 {Code}。", process.ExitCode);
                return new UpdateInstallResult(false, $"安装程序返回退出码 {process.ExitCode}，升级可能未完成。");
            }

            _logger.LogInformation("升级安装完成。");
            _ = relaunchAfterInstall;
            return new UpdateInstallResult(true, "升级完成。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "运行安装程序失败：{Path}", installerPath);
            return new UpdateInstallResult(false, $"启动安装程序失败：{ex.Message}");
        }
    }

    private static async Task<bool> WaitForExitAsync(
        System.Diagnostics.Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    /// <summary>读取当前程序集版本。</summary>
    private SemanticVersion ReadCurrentVersion()
    {
        var informational = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion 形如 "1.1.0+<commit>"，SemanticVersion.TryParse 会自动丢弃 + 之后的部分
        if (SemanticVersion.TryParse(informational, out var parsed))
        {
            return parsed;
        }

        var version = typeof(UpdateService).Assembly.GetName().Version;
        return version is null
            ? new SemanticVersion(0, 0, 0)
            : new SemanticVersion(version.Major, version.Minor, Math.Max(version.Build, 0));
    }

    /// <summary>判断当前是否为「安装版」：安装目录下存在 Inno Setup 的卸载器。</summary>
    private bool DetectInstalled()
    {
        try
        {
            var directory = Path.GetDirectoryName(Environment.ProcessPath);
            return !string.IsNullOrEmpty(directory) && File.Exists(Path.Combine(directory, "unins000.exe"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检测安装形态失败。");
            return false;
        }
    }
}
