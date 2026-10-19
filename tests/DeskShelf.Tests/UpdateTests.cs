using System.Net.Http;
using DeskShelf.App.ViewModels;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Settings;
using DeskShelf.Domain.ValueObjects;
using DeskShelf.Infrastructure.Updates;
using DeskShelf.Infrastructure.Windows;
using DeskShelf.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 版本号解析与比较。这是更新检查的正确性基础：判断错就会「提示有更新但装不上」或
/// 「有新版本却不提示」。
/// </summary>
public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("v1.2.3", 1, 2, 3, null)]
    [InlineData("V2.0.0", 2, 0, 0, null)]
    [InlineData("1.2", 1, 2, 0, null)]
    [InlineData("1.2.3.4", 1, 2, 3, null)]
    [InlineData("1.2.3+abc123", 1, 2, 3, null)]
    [InlineData("v1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("1.2.3-beta", 1, 2, 3, "beta")]
    public void TryParse_AcceptsCommonForms(string text, int major, int minor, int patch, string? prerelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("x.y.z")]
    public void TryParse_RejectsInvalid(string? text)
        => Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Compare_OrdersByMajorMinorPatch()
    {
        var older = new SemanticVersion(1, 1, 0);
        var newer = new SemanticVersion(1, 2, 0);
        var newest = new SemanticVersion(2, 0, 0);

        Assert.True(newer.IsNewerThan(older));
        Assert.True(newest.IsNewerThan(newer));
        Assert.False(older.IsNewerThan(newer));
        Assert.True(newer > older);
        Assert.True(older < newer);

        // 自身比较应相等（同时覆盖运算符实现）
        var same = new SemanticVersion(1, 2, 0);
        Assert.True(same >= newer);
        Assert.True(same <= newer);
        Assert.Equal(0, same.CompareTo(newer));
    }

    [Fact]
    public void Compare_StableIsNewerThanPrereleaseOfSameVersion()
    {
        var stable = new SemanticVersion(1, 2, 0);
        var prerelease = new SemanticVersion(1, 2, 0, "rc.1");

        Assert.True(stable.IsNewerThan(prerelease));
        Assert.False(prerelease.IsNewerThan(stable));
    }

    [Fact]
    public void ToString_RoundTrips()
    {
        Assert.Equal("1.2.3", new SemanticVersion(1, 2, 3).ToString());
        Assert.Equal("1.2.3-rc.1", new SemanticVersion(1, 2, 3, "rc.1").ToString());
        Assert.True(SemanticVersion.TryParse(new SemanticVersion(3, 4, 5, "beta").ToString(), out var parsed));
        Assert.Equal(new SemanticVersion(3, 4, 5, "beta"), parsed);
    }
}

/// <summary>发布信息解析（GitHub Release → 领域模型）。</summary>
public sealed class AppReleaseTests
{
    [Fact]
    public void FindInstaller_PicksSetupExeAmongOtherAssets()
    {
        var release = FakeReleaseSource.ReleaseWithInstaller("1.2.0");

        var installer = release.FindInstaller();

        Assert.NotNull(installer);
        Assert.EndsWith("-setup.exe", installer!.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindInstaller_ReturnsNullWhenOnlyPortableExists()
    {
        var release = new AppRelease(
            new SemanticVersion(1, 2, 0),
            "v1.2.0",
            false,
            "https://example.invalid",
            new[] { new ReleaseAsset("DeskShelf-1.2.0-portable-win-x64.zip", "https://example.invalid/z.zip", 1) },
            null);

        Assert.Null(release.FindInstaller());
    }
}

/// <summary>设置窗口里的更新流程。</summary>
public sealed class UpdateFlowTests
{
    [Fact]
    public async Task Check_WhenNewerVersionExists_ReportsUpdateAndEnablesInstall()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService
        {
            CurrentVersion = new SemanticVersion(1, 1, 0),
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                new SemanticVersion(1, 1, 0),
                new SemanticVersion(1, 2, 0),
                FakeReleaseSource.ReleaseWithInstaller("1.2.0"),
                "发现新版本 1.2.0。"),
        };
        var vm = Create(ctx, updates);

        await vm.CheckForUpdatesAsync();

        Assert.True(vm.CanInstallUpdate);
        Assert.Contains("1.2.0", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.False(vm.IsUpdateBusy);
    }

    [Fact]
    public async Task Check_WhenUpToDate_DoesNotOfferInstall()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService { CurrentVersion = new SemanticVersion(1, 1, 0) };
        var vm = Create(ctx, updates);

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.CanInstallUpdate);
        Assert.Contains("已是最新版本", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_WhenReleaseHasNoInstaller_ExplainsManualDownload()
    {
        var ctx = new ShelfTestContext();
        var release = new AppRelease(
            new SemanticVersion(1, 2, 0),
            "v1.2.0",
            false,
            "https://example.invalid/releases/tag/v1.2.0",
            new[] { new ReleaseAsset("DeskShelf-1.2.0-portable-win-x64.zip", "https://example.invalid/z.zip", 1) },
            null);

        var updates = new FakeUpdateService
        {
            CheckResult = new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                new SemanticVersion(1, 1, 0),
                new SemanticVersion(1, 2, 0),
                release,
                "发现新版本 1.2.0，但该版本没有提供安装包，请到发布页手动下载。"),
        };
        var vm = Create(ctx, updates);

        await vm.CheckForUpdatesAsync();

        Assert.False(vm.CanInstallUpdate);
        Assert.Contains("手动下载", vm.UpdateStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Install_ForInstalledBuild_DownloadsRunsInstallerAndShutsDown()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService { IsInstalled = true };
        updates.CheckResult = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new SemanticVersion(1, 1, 0),
            new SemanticVersion(1, 2, 0),
            FakeReleaseSource.ReleaseWithInstaller("1.2.0"),
            "发现新版本 1.2.0。");

        var shutdownCalled = false;
        var vm = Create(ctx, updates);
        vm.ShutdownApplicationAsync = () =>
        {
            shutdownCalled = true;
            return Task.CompletedTask;
        };

        await vm.CheckForUpdatesAsync();
        await vm.InstallUpdateAsync();

        Assert.Equal(1, updates.DownloadCount);
        Assert.Equal(1, updates.InstallCount);
        Assert.True(shutdownCalled, "安装成功后应请求关闭应用以便替换文件");
    }

    [Fact]
    public async Task Install_ForPortableBuild_DoesNotRunInstaller()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService { IsInstalled = false };
        updates.CheckResult = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new SemanticVersion(1, 1, 0),
            new SemanticVersion(1, 2, 0),
            FakeReleaseSource.ReleaseWithInstaller("1.2.0"),
            "发现新版本 1.2.0。");

        var vm = Create(ctx, updates);
        await vm.CheckForUpdatesAsync();
        await vm.InstallUpdateAsync();

        Assert.Equal(1, updates.DownloadCount);
        Assert.Equal(0, updates.InstallCount);   // 便携版不能自动替换正在运行的文件
        Assert.False(vm.IsInstalledBuild);
        Assert.False(string.IsNullOrWhiteSpace(vm.PortableHint));
    }

    [Fact]
    public async Task Install_WhenDownloadFails_ReportsErrorAndKeepsUiUsable()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService { FailDownload = true };
        updates.CheckResult = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new SemanticVersion(1, 1, 0),
            new SemanticVersion(1, 2, 0),
            FakeReleaseSource.ReleaseWithInstaller("1.2.0"),
            "发现新版本 1.2.0。");

        var vm = Create(ctx, updates);
        await vm.CheckForUpdatesAsync();
        await vm.InstallUpdateAsync();

        Assert.Contains("更新失败", vm.UpdateStatus, StringComparison.Ordinal);
        Assert.False(vm.IsUpdateBusy);
        Assert.False(vm.IsUpdateProgressVisible);
    }

    [Fact]
    public async Task Install_WhenInstallerFails_DoesNotShutDown()
    {
        var ctx = new ShelfTestContext();
        var updates = new FakeUpdateService
        {
            InstallResult = new UpdateInstallResult(false, "安装程序返回退出码 2，升级可能未完成。"),
        };
        updates.CheckResult = new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new SemanticVersion(1, 1, 0),
            new SemanticVersion(1, 2, 0),
            FakeReleaseSource.ReleaseWithInstaller("1.2.0"),
            "发现新版本 1.2.0。");

        var shutdownCalled = false;
        var vm = Create(ctx, updates);
        vm.ShutdownApplicationAsync = () =>
        {
            shutdownCalled = true;
            return Task.CompletedTask;
        };

        await vm.CheckForUpdatesAsync();
        await vm.InstallUpdateAsync();

        Assert.False(shutdownCalled, "安装失败时不应关闭应用");
        Assert.Contains("退出码", vm.UpdateStatus, StringComparison.Ordinal);
    }

    /// <summary>
    /// 限流时必须给出可操作的信息（何时可重试 / 可用令牌提升配额），
    /// 而不是笼统的「网络错误」—— 未认证配额只有 60 次/小时，真实用户很容易撞上。
    /// </summary>
    [Fact]
    public async Task UpdateService_WhenRateLimited_ExplainsQuotaAndRetryTime()
    {
        var ctx = new ShelfTestContext();
        var releases = new FakeReleaseSource
        {
            Failure = ReleaseQueryFailure.RateLimited,
            RetryAfter = new DateTimeOffset(2026, 1, 1, 15, 30, 0, TimeSpan.FromHours(8)),
        };
        var service = new UpdateService(
            releases,
            ctx.Settings,
            new DeskShelf.Infrastructure.Storage.AppPaths(Path.Combine(Path.GetTempPath(), "DeskShelfTest-" + Guid.NewGuid().ToString("N"))),
            new HttpClient(),
            NullLogger<UpdateService>.Instance);

        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("次数已用尽", result.Message, StringComparison.Ordinal);
        Assert.Contains("15:30", result.Message, StringComparison.Ordinal);
        Assert.Contains("令牌", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateService_PassesConfiguredTokenToReleaseSource()
    {
        var ctx = new ShelfTestContext();
        await ctx.Settings.SaveAsync(ShelfSettings.Default with { GitHubToken = "  ghp_test_token  " });

        var releases = new FakeReleaseSource { Latest = FakeReleaseSource.ReleaseWithInstaller("9.9.9") };
        var service = new UpdateService(
            releases,
            ctx.Settings,
            new DeskShelf.Infrastructure.Storage.AppPaths(Path.Combine(Path.GetTempPath(), "DeskShelfTest-" + Guid.NewGuid().ToString("N"))),
            new HttpClient(),
            NullLogger<UpdateService>.Instance);

        await service.CheckAsync();

        Assert.Equal("ghp_test_token", releases.LastAccessToken?.Trim());
    }

    // ————————————————————————— 更新 helper —————————————————————————

    /// <summary>
    /// 更新 helper 脚本必须满足的顺序要求，缺一就会重演「退出码 1」的失败：
    /// 先等本进程退出（否则安装包因 AppMutex 拒绝安装），再静默安装，最后重启。
    /// </summary>
    [Fact]
    public void BuildHelperScript_WaitsForThisProcessThenInstallsThenRelaunches()
    {
        var script = UpdateService.BuildHelperScript(
            processId: 4242,
            installerPath: @"C:\Users\me\AppData\Roaming\DeskShelf\updates\1.2.1-setup.exe",
            exePath: @"C:\Users\me\AppData\Local\Programs\DeskShelf\DeskShelf.exe",
            relaunch: true);

        // 1) 等待本进程退出（AppMutex 检查的前提）
        Assert.Contains("tasklist", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4242", script, StringComparison.Ordinal);

        // 2) 用静默参数运行下载到的安装包
        Assert.Contains("1.2.1-setup.exe", script, StringComparison.Ordinal);
        Assert.Contains(UpdateService.SilentInstallerArguments, script, StringComparison.Ordinal);

        // 3) 顺序必须在「等待」之后（用索引比较，避免"存在但顺序错"的假通过）
        var waitIndex = script.IndexOf(":wait", StringComparison.Ordinal);
        var installIndex = script.IndexOf(":install", StringComparison.Ordinal);
        Assert.True(waitIndex >= 0 && installIndex > waitIndex, "安装步骤必须位于等待步骤之后");

        // 4) 重启新版本，且必须在安装之后
        Assert.Contains("DeskShelf.exe", script, StringComparison.Ordinal);
        var relaunchIndex = script.LastIndexOf("start \"\"", StringComparison.Ordinal);
        Assert.True(relaunchIndex > installIndex, "重启步骤必须在安装之后");
    }

    [Fact]
    public void BuildHelperScript_WhenRelaunchDisabled_StillInstallsButDoesNotRestart()
    {
        var script = UpdateService.BuildHelperScript(
            processId: 1,
            installerPath: @"C:\tmp\setup.exe",
            exePath: @"C:\tmp\DeskShelf.exe",
            relaunch: false);

        Assert.Contains(UpdateService.SilentInstallerArguments, script, StringComparison.Ordinal);
        Assert.DoesNotContain(@"{exePath}", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SilentInstallerArguments_DoNotRelyOnForceCloseApplications()
    {
        // /FORCECLOSEAPPLICATIONS 无法绕过 AppMutex 的「程序正在运行」提示：
        // 弹窗被 SUPPRESS 后默认选择取消，安装直接中止（退出码 1）。
        Assert.DoesNotContain("FORCECLOSE", UpdateService.SilentInstallerArguments, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>待办 B1：安装成功后删除安装包，避免每次升级残留 ~64 MB。</summary>
    [Fact]
    public void BuildHelperScript_DeletesInstallerOnlyOnSuccess()
    {
        var script = UpdateService.BuildHelperScript(
            processId: 7,
            installerPath: @"C:\updates\setup.exe",
            exePath: @"C:\app\DeskShelf.exe",
            relaunch: false);

        // 必须先捕获退出码：del 自身会覆盖 ERRORLEVEL
        Assert.Contains("set \"RC=%ERRORLEVEL%\"", script, StringComparison.Ordinal);

        // 删除动作受 RC==0 保护 —— 失败时保留安装包以便重试
        var deleteLine = script
            .Split('\n')
            .Select(l => l.Trim())
            .Single(l => l.Contains("del /f /q", StringComparison.OrdinalIgnoreCase));

        Assert.Contains("\"%RC%\"==\"0\"", deleteLine, StringComparison.Ordinal);
        Assert.Contains(@"C:\updates\setup.exe", deleteLine, StringComparison.Ordinal);
    }

    /// <summary>待办 B1：启动清理只动本程序产生的临时文件，不碰已下载的安装包。</summary>
    [Fact]
    public void CleanupDownloadedArtifacts_RemovesHelperAndPartialFilesButKeepsInstaller()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskShelf-clean-" + Guid.NewGuid().ToString("N"));
        var paths = new DeskShelf.Infrastructure.Storage.AppPaths(root);
        paths.EnsureCreated();

        var updatesDir = Path.Combine(root, "updates");
        Directory.CreateDirectory(updatesDir);
        var helper = Path.Combine(updatesDir, "apply-update.cmd");
        var partial = Path.Combine(updatesDir, "1.2.4-setup.exe.part");
        var installer = Path.Combine(updatesDir, "1.2.3-setup.exe");
        File.WriteAllText(helper, "@echo off");
        File.WriteAllText(partial, "half");
        File.WriteAllText(installer, "full");

        try
        {
            var service = new UpdateService(
                new FakeReleaseSource(),
                new FakeSettingsStore(),
                paths,
                new HttpClient(),
                NullLogger<UpdateService>.Instance);

            service.CleanupDownloadedArtifacts();

            Assert.False(File.Exists(helper), "helper 脚本应被清理");
            Assert.False(File.Exists(partial), "未完成的 .part 应被清理");
            Assert.True(File.Exists(installer), "已下载的安装包必须保留（升级失败时用于重试）");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // 清理失败不影响测试结论
            }
        }
    }

    private static SettingsViewModel Create(ShelfTestContext ctx, FakeUpdateService updates)
        => new(
            ctx.Settings,
            new RecordingHotkeyService(),
            new AutoStartService(NullLogger<AutoStartService>.Instance),
            ctx.Service,
            new NoopThemeApplier(),
            updates,
            ctx.Launcher,
            new FakeUiDispatcher(),
            () => Task.CompletedTask,
            NullLogger<SettingsViewModel>.Instance);

    private sealed class NoopThemeApplier : DeskShelf.App.Services.IThemeApplier
    {
        public void ApplyFromSettings()
        {
        }

        public void Preview(bool light)
        {
        }
    }

    private sealed class RecordingHotkeyService : IHotkeyService
    {
        public bool IsRegistered => RegisteredGesture is not null;

        public HotkeyGesture? RegisteredGesture { get; private set; }

        public event EventHandler? Pressed;

        public bool TryRegister(HotkeyGesture gesture)
        {
            RegisteredGesture = gesture;
            return true;
        }

        public void Unregister() => RegisteredGesture = null;

        private void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
    }
}
