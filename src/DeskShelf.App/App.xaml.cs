using System.Windows;
using System.Windows.Threading;
using DeskShelf.App.Services;
using DeskShelf.App.ViewModels;
using DeskShelf.App.Views;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Infrastructure;
using DeskShelf.Infrastructure.Hotkeys;
using DeskShelf.Infrastructure.Storage;
using DeskShelf.Infrastructure.Theming;
using DeskShelf.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App;

/// <summary>
/// 应用启动与组合根。
/// 职责：注册服务、单实例互斥、全局异常兜底、创建面板窗口与托盘图标、
/// 注册全局热键、应用主题，并在退出时保存状态。
/// </summary>
/// <remarks>
/// 本类同时实现 <see cref="IThemeApplier"/>：主题资源字典由 Application 持有，
/// 设置窗口通过该接口请求换肤，避免 ViewModel 反向依赖 App。
/// </remarks>
public partial class App : System.Windows.Application, IThemeApplier
{
    private const string InstanceMutexName = @"Global\DeskShelf.SingleInstance";

    private Mutex? _instanceMutex;
    private SingleInstanceActivation? _activation;
    private ServiceProvider? _services;
    private PanelWindow? _panel;
    private TrayIconHost? _tray;
    private ILogger<App>? _logger;

    /// <summary>本次启动是否完成了「旧数据目录 → 新数据目录」的迁移（用于落日志）。</summary>
    private bool _migratedFromLegacy;

    /// <summary>服务提供器（供托盘等在同一进程内解析服务）。</summary>
    internal IServiceProvider Services => _services
        ?? throw new InvalidOperationException("服务容器尚未初始化。");

    /// <inheritdoc />
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            await StartAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 启动阶段没有 UI 可承载异常，必须显式兜底，否则表现为「双击没反应」
            try
            {
                _logger?.LogCritical(ex, "启动失败。");
            }
            catch (Exception)
            {
                // 日志不可用时忽略
            }

            MessageBox.Show(
                $"桌面置物架启动失败：{ex.Message}\n\n详细信息见 %APPDATA%\\DeskShelf\\logs。",
                "桌面置物架",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown();
        }
    }

    private async Task StartAsync()
    {
        // 单实例：多开会导致热键注册失败与数据文件竞争
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 用户再次点击快捷方式时的正确行为是「把已有面板叫出来」，而不是弹提示框就退出 ——
            // 后者从用户视角就是「点了没反应 / 打不开」（尤其是面板已收起、托盘图标藏在折叠区时）。
            if (SingleInstanceActivation.TrySignalExisting())
            {
                Shutdown();
                return;
            }

            // 唤醒通道不可用（极少见：权限或对象被占用）时退回提示框，至少让用户知道程序在运行
            MessageBox.Show(
                "桌面置物架已经在运行（请查看任务栏通知区域）。",
                "桌面置物架",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 尽早创建唤醒事件：即使后启动的实例在面板就绪前就发来请求，
        // 自动重置事件会保持有信号，等稍后注册监听时立刻被消费，不会丢。
        _activation = new SingleInstanceActivation();
        if (!_activation.Initialize())
        {
            _activation = null;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        _services = BuildServices();
        _services.UseDebouncedPersistence();
        _logger = _services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("桌面置物架 DeskShelf 启动，版本 {Version}。", typeof(App).Assembly.GetName().Version);

        if (_migratedFromLegacy)
        {
            _logger.LogInformation("已把旧版数据目录 %APPDATA%\\DeskTools 迁移到 %APPDATA%\\DeskShelf。");
        }
        // 清理上次升级可能残留的 helper 脚本与未完成下载（不删已下载的安装包，便于失败重试）
        try
        {
            _services!.GetService<IUpdateService>()?.CleanupDownloadedArtifacts();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理下载目录残留失败。");
        }

        VerifyUnitCatalog();

        // 必须在面板与缩略图加载器创建之前跑：否则 ThumbnailLoader 会缓存住透明图
        RepairInvisibleImagesOnce();

        // 先读设置：窗口定位与热键都依赖它（缺失文件时内部回退默认值）
        try
        {
            await _services.GetRequiredService<ISettingsStore>().LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "加载设置失败，使用默认设置。");
        }

        ApplyTheme(IsLightTheme());

        _panel = _services.GetRequiredService<PanelWindow>();
        _tray = _services.GetRequiredService<TrayIconHost>();
        _tray.Attach(this, _panel);

        RegisterHotkey();

        // 监听「再次启动」请求：走 Dispatcher 切回 UI 线程再显示面板
        _activation?.Listen(() => Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    _logger?.LogInformation("收到再次启动请求，显示面板。");
                    _panel?.ShowPanel();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "响应再次启动请求失败。");
                }
            }),
            DispatcherPriority.Normal));

        // 首发即显示面板（启动后直接可用；后续可在设置里改为静默启动）
        _panel.ShowPanel();
    }

    /// <inheritdoc />
    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_panel is not null)
            {
                await _panel.PersistAsync().ConfigureAwait(true);
            }

            // 退出前把防抖中的内容强制落盘，避免丢数据
            _services?.GetService<DeskShelf.Infrastructure.Persistence.DebouncedSaver>()?.FlushAsync().Wait(TimeSpan.FromSeconds(2));

            if (_services?.GetService<IHotkeyService>() is { } hotkey)
            {
                hotkey.Unregister();
            }

            _tray?.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "退出清理时发生异常。");
        }
        finally
        {
            _services?.Dispose();
            _activation?.Dispose();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    private ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        var paths = new AppPaths();

        // 项目由 DeskTools 更名为 DeskShelf：首次运行时把旧数据目录整体迁过来，
        // 避免用户升级后看到空面板。迁移失败不影响启动（新目录随后创建）。
        _migratedFromLegacy = paths.TryMigrateFromLegacy();
        paths.EnsureCreated();

        services.AddLogging(builder =>
        {
            builder.AddDebug();
            builder.AddProvider(new Infrastructure.Logging.FileLoggerProvider(paths.Logs, LogLevel.Information));
        });

        // 基础设施 + 用例服务（一行注册，表现层不接触具体实现）
        services.AddDeskShelfInfrastructure();
        // 表现层服务
        services.AddSingleton<ImageBytesCache>();
        services.AddSingleton<ThumbnailLoader>(sp => new ThumbnailLoader(
            sp.GetRequiredService<ImageBytesCache>(),
            sp.GetRequiredService<IImageBytesSource>(),
            sp.GetRequiredService<ILogger<ThumbnailLoader>>()));
        services.AddSingleton<SampleDataSeeder>();
        services.AddSingleton<ThumbnailLoadScheduler>();
        services.AddSingleton<IThemeApplier>(this);
        services.AddSingleton<PanelViewModel>();
        services.AddSingleton<PanelWindow>();

        // 设置：ViewModel 单例（持有打开时的设置快照用于「取消」回滚），窗口按需新建。
        // 窗口必须是 Transient —— WPF 的 Window 一旦 Close 就无法再次 Show，
        // 注册成单例会让「打开设置 → 关闭 → 再打开」直接抛异常。
        services.AddSingleton<SettingsViewModel>(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<IHotkeyService>(),
            sp.GetRequiredService<AutoStartService>(),
            sp.GetRequiredService<ShelfService>(),
            sp.GetRequiredService<IThemeApplier>(),
            sp.GetRequiredService<IUpdateService>(),
            sp.GetRequiredService<ISystemLauncher>(),
            sp.GetRequiredService<IUiDispatcher>(),
            () => sp.GetRequiredService<ISystemLauncher>().OpenPathAsync(paths.Root),
            sp.GetRequiredService<ILogger<SettingsViewModel>>()));
        services.AddTransient(sp => new SettingsWindow(sp.GetRequiredService<SettingsViewModel>()));
        services.AddSingleton<TrayIconHost>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>设置窗口请求：按当前设置应用主题。</summary>
    public void ApplyFromSettings()
        => ApplyTheme(IsLightTheme());

    /// <summary>设置窗口请求：临时预览指定明暗（不写设置）。</summary>
    public void Preview(bool light) => ApplyTheme(light);

    /// <summary>启动自检：确认 unit 枚举的每个值都在 <c>UnitCatalog</c> 中登记。</summary>
    private void VerifyUnitCatalog()
    {
        var missing = Application.Catalog.UnitCatalog.FindUnregisteredKinds();
        if (missing.Count > 0)
        {
            _logger?.LogError("以下 unit 类型未在 UnitCatalog 中注册：{Kinds}", string.Join(", ", missing));
        }
    }

    private void RegisterHotkey()
    {
        if (_services?.GetService<IHotkeyService>() is not { } hotkey)
        {
            return;
        }

        hotkey.Pressed += OnHotkeyPressed;

        var gesture = _services.GetRequiredService<ISettingsStore>().Current.Hotkey;
        if (hotkey.TryRegister(gesture))
        {
            return;
        }

        _logger?.LogWarning("全局热键 {Gesture} 注册失败。", gesture.DisplayText);
        _tray?.ShowWarning($"全局热键 {gesture.DisplayText} 被其他程序占用，可在设置中更换。");
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => _panel?.ToggleVisibility()), DispatcherPriority.Normal);

    /// <summary>
    /// 一次性修复历史上被存成「全透明」的图片（v1.4.0 及更早版本的剪贴板缺陷）。
    /// </summary>
    /// <remarks>
    /// 修复本身是幂等的，但每次启动都解码整个图片目录不值得，因此用标记文件保证只扫一次。
    /// 失败时不写标记，下次启动会自然重试 —— 修复失败绝不该影响启动。
    /// </remarks>
    private void RepairInvisibleImagesOnce()
    {
        const string markerName = ".image-repair-v1";

        try
        {
            if (_services is null)
            {
                return;
            }

            var paths = _services.GetRequiredService<AppPaths>();
            var marker = Path.Combine(paths.Root, markerName);
            if (File.Exists(marker))
            {
                return;
            }

            var repaired = _services.GetRequiredService<ImageFileStore>().RepairInvisibleImages();
            File.WriteAllText(marker, DateTimeOffset.Now.ToString("O"));

            if (repaired > 0)
            {
                _logger?.LogInformation("已修复 {Count} 张历史上显示为全透明的图片。", repaired);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "修复全透明图片失败，下次启动将重试。");
        }
    }

    private bool IsLightTheme()
        => _services?.GetRequiredService<ISettingsStore>().Current.Theme switch
        {
            AppTheme.Light => true,
            AppTheme.Dark => false,
            _ => SystemThemeDetector.IsLightTheme(),
        };

    /// <summary>
    /// 应用主题：整体替换令牌资源字典（浅色 / 深色同名键），
    /// 界面全部使用 DynamicResource，因此无需重启或重建视图。
    /// </summary>
    /// <param name="light">是否应用浅色；由调用方决定（设置里可能是「跟随系统」）。</param>
    private void ApplyTheme(bool light)
    {
        var source = light ? "Themes/Tokens.Light.xaml" : "Themes/Tokens.Dark.xaml";

        try
        {
            // 注意：使用绝对 pack URI 而非相对 URI —— 相对 URI 会按 BaseUri 折叠，
            // 且 WPF 会按 URI 缓存资源字典，容易拿到上一次（浅色）的实例。
            var dictionary = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/DeskShelf;component/{source}", UriKind.Absolute),
            };

            // 约定：令牌字典始终是合并字典中的第 0 项
            var merged = Resources.MergedDictionaries;
            if (merged.Count == 0)
            {
                merged.Add(dictionary);
            }
            else
            {
                merged[0] = dictionary;
            }

            _logger?.LogInformation("主题已应用：{Theme}（窗口背景 {Color}）。",
                light ? "浅色" : "深色",
                (Resources["Brush.Window.Background"] as System.Windows.Media.SolidColorBrush)?.Color.ToString() ?? "?");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "应用主题失败，保持当前主题。");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 记录完整堆栈：这类问题的定位完全依赖堆栈
        _logger?.LogError(e.Exception, "UI 线程未处理异常：{Type}", e.Exception.GetType().Name);
        _panel?.DataContext.As<PanelViewModel>()?.ShowStatus("发生未预期的错误，已记录日志。");
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        => _logger?.LogCritical(e.ExceptionObject as Exception, "后台线程未处理异常。");

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "未观测的任务异常。");
        e.SetObserved();
    }
}

/// <summary>内联的小工具：安全地做类型转换（避免在多处写 as + null 检查）。</summary>
internal static class ObjectExtensions
{
    /// <summary>尝试转换为目标类型。</summary>
    public static T? As<T>(this object? value)
        where T : class => value as T;
}
