using System.Net.Http;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Undo;
using DeskShelf.Domain.Shelf;
using DeskShelf.Infrastructure.Clipboard;
using DeskShelf.Infrastructure.Content;
using DeskShelf.Infrastructure.Dispatching;
using DeskShelf.Infrastructure.Hotkeys;
using DeskShelf.Infrastructure.Imaging;
using DeskShelf.Infrastructure.Launchers;
using DeskShelf.Infrastructure.Logging;
using DeskShelf.Infrastructure.Persistence;
using DeskShelf.Infrastructure.Storage;
using DeskShelf.Infrastructure.Updates;
using DeskShelf.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Infrastructure;

/// <summary>
/// 基础设施层的组合根：把 OS 适配实现与用例服务注册进 DI 容器。
/// 表现层（App）只调用本扩展方法，不知道任何具体实现类型。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册基础设施与用例服务。</summary>
    /// <param name="services">服务集合。</param>
    /// <remarks>
    /// 日志管道的注册由宿主（App 层）负责 —— 基础设施层只依赖日志抽象，
    /// 因此不在此处调用 <c>AddLogging</c>（那会引入完整的 Logging 包）。
    /// </remarks>
    public static IServiceCollection AddDeskShelfInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // —— 目录布局（单一实例，供存储与启动器共用） ——
        var paths = new AppPaths();
        paths.EnsureCreated();
        services.AddSingleton(paths);

        // —— 领域聚合 ——
        services.AddSingleton<Shelf>();
        services.AddSingleton<UndoStack>(_ => new UndoStack());

        // —— 系统适配层 ——
        services.AddSingleton<IUiDispatcher>(_ => new WpfUiDispatcher());
        // 具体类型也注册一次：启动时的一次性图片修复需要直接调用它（见 App.xaml.cs）
        services.AddSingleton(sp => new ImageFileStore(
            paths.Images,
            sp.GetRequiredService<ILogger<ImageFileStore>>()));
        services.AddSingleton<IImageStore>(sp => sp.GetRequiredService<ImageFileStore>());
        services.AddSingleton<IUnitContentFactory, UnitContentFactory>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<ISystemLauncher, ShellLauncher>();
        services.AddSingleton<IShelfStore, JsonShelfStore>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IDisplayService, DisplayService>();
        services.AddSingleton<IHotkeyService, GlobalHotkeyService>();
        services.AddSingleton<AutoStartService>();
        services.AddSingleton<IImageBytesSource, ImageBytesSource>();

        // —— 更新（检查 GitHub Releases → 下载安装包 → 静默升级）——
        // 单个 HttpClient 复用（避免端口耗尽）；User-Agent 是 GitHub API 的硬性要求。
        services.AddSingleton(_ =>
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DeskShelf-Updater");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        });
        services.AddSingleton<IReleaseSource, GitHubReleaseSource>();
        services.AddSingleton<IUpdateService, UpdateService>();

        services.AddSingleton(sp => new DebouncedSaver(
            async token => await sp.GetRequiredService<ShelfService>().SaveAsync(token).ConfigureAwait(false)));

        // —— 用例服务（唯一逻辑入口） ——
        services.AddSingleton<ShelfService>();

        return services;
    }

    /// <summary>
    /// 把「置物架内容变化」接到防抖保存上：连续粘贴多条只会产生一次磁盘写入。
    /// 需在容器构建后调用一次。
    /// </summary>
    /// <param name="services">已构建的服务提供器。</param>
    public static IServiceProvider UseDebouncedPersistence(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var shelf = services.GetRequiredService<ShelfService>();
        var saver = services.GetRequiredService<DebouncedSaver>();
        shelf.Changed += (_, _) => saver.Request();
        return services;
    }
}
