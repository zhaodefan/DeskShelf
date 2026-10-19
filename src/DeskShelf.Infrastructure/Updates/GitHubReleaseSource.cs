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

    /// <summary>
    /// 清理 updates 目录里的残留文件（启动时调用）。
    /// </summary>
    /// <remarks>
    /// 只删两类**由本程序自己产生**的临时文件，绝不触碰其它内容：
    /// <list type="bullet">
    /// <item><c>apply-update.cmd</c>：上次升级用过的 helper 脚本，任务结束后已无用；</item>
    /// <item><c>*.part</c>：上次下载到一半的临时文件（真正的安装包由 helper 在升级成功后删除）。</item>
    /// </list>
    /// 安装包本身**故意不删**：若上次升级失败，保留它便于用户重试与排查。
    /// </remarks>
    public void CleanupDownloadedArtifacts()
    {
        var directory = System.IO.Path.Combine(_paths.Root, DownloadFolderName);
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            var helper = System.IO.Path.Combine(directory, "apply-update.cmd");
            if (File.Exists(helper))
            {
                File.Delete(helper);
                _logger.LogDebug("已清理上次升级使用的 helper 脚本。");
            }

            foreach (var stale in Directory.EnumerateFiles(directory, "*.part"))
            {
                File.Delete(stale);
                _logger.LogDebug("已清理未完成的下载临时文件：{Path}", stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响启动
            _logger.LogWarning(ex, "清理下载目录残留失败。");
        }
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

        // 这里不再 report(1.0)：进度回调经 Dispatcher 异步投递，若在下载结束后再发一次，
        // 会覆盖调用方随后设置的「下载完成 / 准备安装 / 失败」提示。
        // 收尾进度由调用方负责设置。
        return target;
    }

    /// <summary>安装程序使用的静默参数。</summary>
    /// <remarks>
    /// <para><c>/VERYSILENT</c>：由 helper 进程在后台启动，不需要任何界面。</para>
    /// <para><c>/SUPPRESSMSGBOXES</c> + <c>/NORESTART</c> + <c>/NOCANCEL</c>：不弹窗、不重启、不可被打断。</para>
    /// <para>
    /// 注意<b>不能</b>依赖 <c>/FORCECLOSEAPPLICATIONS</c> 解决「程序正在运行」的互斥体提示：
    /// Inno Setup 的 AppMutex 检查发生在应用关闭机制之前，弹窗被 SUPPRESS 后会默认选择「取消」，
    /// 导致退出码 1。正确做法见 <see cref="RunInstallerAsync"/>：先用一个独立的 helper 进程
    /// 等本程序退出，再由它执行安装。
    /// </para>
    /// </remarks>
    public const string SilentInstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCANCEL";

    /// <inheritdoc />
    /// <remarks>
    /// 为什么不能直接启动安装程序：安装包设置了 <c>AppMutex</c>，检测到本程序仍在运行时
    /// 会弹窗要求先关闭；该弹窗被 <c>/SUPPRESSMSGBOXES</c> 压掉后自动选择「取消」，
    /// 安装直接中止（退出码 1）。
    ///
    /// 因此这里改用一个 **helper 进程**：它先等本进程退出（安装包才能顺利接手），
    /// 然后静默运行安装程序，最后重新启动新版本。本方法返回后调用方应立即关闭应用。
    /// </remarks>
    public Task<UpdateInstallResult> RunInstallerAsync(
        string installerPath,
        bool relaunchAfterInstall = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return Task.FromResult(new UpdateInstallResult(false, "安装包不存在，请重新下载。"));
        }

        try
        {
            var currentProcessId = Environment.ProcessId;
            var exePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(exePath))
            {
                return Task.FromResult(new UpdateInstallResult(false, "无法定位当前程序路径，请手动安装。"));
            }

            var helperDirectory = System.IO.Path.Combine(_paths.Root, DownloadFolderName);
            Directory.CreateDirectory(helperDirectory);

            var helperPath = System.IO.Path.Combine(helperDirectory, "apply-update.cmd");
            File.WriteAllText(helperPath, BuildHelperScript(currentProcessId, installerPath, exePath, relaunchAfterInstall), System.Text.Encoding.Default);

            _logger.LogInformation(
                "启动更新 helper（等待 PID {Pid} 退出后安装 {Installer}）。",
                currentProcessId, Path.GetFileName(installerPath));

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,

                // 完全脱离本进程：否则关闭应用时可能连带终止 helper
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                WorkingDirectory = helperDirectory,
            };

            var helper = System.Diagnostics.Process.Start(startInfo);
            if (helper is null)
            {
                return Task.FromResult(new UpdateInstallResult(false, "无法启动更新程序。"));
            }

            _logger.LogInformation("更新 helper 已启动（PID {Pid}）。", helper.Id);
            return Task.FromResult(new UpdateInstallResult(
                true,
                "正在安装更新，程序即将退出并自动重启。",
                RelaunchRequired: relaunchAfterInstall));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动更新 helper 失败：{Path}", installerPath);
            return Task.FromResult(new UpdateInstallResult(false, $"启动更新程序失败：{ex.Message}"));
        }
    }

    /// <summary>
    /// 生成 helper 脚本内容。
    /// </summary>
    /// <remarks>
    /// 用批处理而不是把逻辑塞进命令行的原因：需要「等待进程退出 + 顺序执行 + 重新启动」三步，
    /// 批处理最直白、无额外依赖，且失败路径能同时保留可诊断信息（日志文件）。
    /// </remarks>
    public static string BuildHelperScript(int processId, string installerPath, string exePath, bool relaunch)
    {
        // 日志放在 %TEMP% 下：路径不含引号，避免批处理里 set 语句被嵌套引号破坏
        var logPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"DeskShelf-update-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        // 用 StringBuilder 保证行尾统一为 CRLF（批处理对换行敏感）
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal enabledelayedexpansion");
        sb.AppendLine($"set \"LOG={logPath}\"");
        sb.AppendLine("echo update helper started >> \"%LOG%\"");
        sb.AppendLine();

        // 1) 等本程序退出（最多约 60 秒）。AppMutex 检查要求进程真的消失。
        sb.AppendLine($"echo wait for pid {processId} >> \"%LOG%\"");
        sb.AppendLine("set /a TRIES=0");
        sb.AppendLine(":wait");
        sb.AppendLine($"tasklist /FI \"PID eq {processId}\" 2>nul | find \"{processId}\" >nul");
        sb.AppendLine("if errorlevel 1 goto install");
        sb.AppendLine("set /a TRIES+=1");
        sb.AppendLine("if !TRIES! GEQ 60 goto force");
        sb.AppendLine("ping -n 2 127.0.0.1 >nul");
        sb.AppendLine("goto wait");
        sb.AppendLine();

        // 2) 超时后强制结束（仅在用户放弃关闭时兜底）
        sb.AppendLine(":force");
        sb.AppendLine("echo timed out, force kill >> \"%LOG%\"");
        sb.AppendLine($"taskkill /PID {processId} /F >nul 2>&1");
        sb.AppendLine("ping -n 3 127.0.0.1 >nul");
        sb.AppendLine();

        // 3) 静默安装
        sb.AppendLine(":install");
        sb.AppendLine("echo run installer >> \"%LOG%\"");
        sb.AppendLine($"start /wait \"\" \"{installerPath}\" {SilentInstallerArguments}");

        // 立刻捕获退出码：后续任何命令（尤其是 del）都会覆盖 ERRORLEVEL
        sb.AppendLine("set \"RC=%ERRORLEVEL%\"");
        sb.AppendLine("echo installer exit code %RC% >> \"%LOG%\"");

        // 3b) 安装成功后删除安装包（每次更新约 64 MB，不清理会一直累积）。
        // 安装失败时**保留**文件，便于用户重试或排查。
        sb.AppendLine("if \"%RC%\"==\"0\" del /f /q \"" + installerPath + "\" >nul 2>&1");
        sb.AppendLine("if \"%RC%\"==\"0\" echo removed installer >> \"%LOG%\"");

        // 4) 无论安装成功与否都尝试重启（成功则启动新版，失败则至少把用户拉回可用状态）
        if (relaunch)
        {
            sb.AppendLine("echo relaunch >> \"%LOG%\"");
            sb.AppendLine($"start \"\" \"{exePath}\"");
        }

        sb.AppendLine("echo done >> \"%LOG%\"");
        sb.AppendLine("endlocal");
        sb.AppendLine("exit");

        return sb.ToString();
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
