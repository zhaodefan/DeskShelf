using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskTools.App.ViewModels;
using DeskTools.App.Views;
using DeskTools.Application.Abstractions;
using DeskTools.Application.Settings;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;

namespace DeskTools.App.Services;

/// <summary>
/// 任务栏通知区域（托盘）图标宿主。
/// 提供「双击切换面板」「面板收起后仍可从托盘呼出」「右键退出」的闭环 ——
/// 这是「固定最小化后仍能找回」得以成立的前提。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    /// <summary>托盘图标资源 URI（必须用 URI 加载，见 <see cref="ApplyIconSource"/> 的说明）。</summary>
    private const string IconResourceUri = "pack://application:,,,/DeskTools;component/Assets/desktools.ico";

    private readonly ISettingsStore _settings;
    private readonly ILogger<TrayIconHost> _logger;

    private TaskbarIcon? _icon;
    private System.Windows.Application? _app;
    private PanelWindow? _panel;

    /// <summary>创建托盘宿主。</summary>
    public TrayIconHost(ISettingsStore settings, ILogger<TrayIconHost> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>创建并挂载托盘图标。</summary>
    public void Attach(System.Windows.Application app, PanelWindow panel)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));

        _icon = new TaskbarIcon
        {
            ToolTipText = "DeskTools（双击呼出面板）",
            Visibility = Visibility.Visible,
            ContextMenu = BuildContextMenu(),
            NoLeftClickDelay = true,
        };

        _icon.TrayLeftMouseDoubleClick += (_, _) => _panel.ToggleVisibility();

        try
        {
            _icon.ForceCreate(enablesEfficiencyMode: false);
            _logger.LogInformation("托盘图标已创建。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建托盘图标失败；热键与面板仍可正常使用。");
            return;
        }

        ApplyIconSource();
    }

    /// <summary>
    /// 设置托盘图标位图。
    /// 注意：H.NotifyIcon 在转换 <see cref="ImageSource"/> 时会读取 <c>BitmapImage.UriSource</c>，
    /// 因此这里必须用<b>资源 URI</b> 加载（从内存流构造的帧没有 UriSource，会抛 UriFormatException）。
    /// </summary>
    private void ApplyIconSource()
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(IconResourceUri, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            _icon.IconSource = image;
        }
        catch (Exception ex)
        {
            // 图标缺失不影响功能：托盘显示默认图标即可
            _logger.LogWarning(ex, "加载托盘图标失败，将使用系统默认图标。");
        }
    }

    /// <summary>在托盘显示一条警告气泡（例如热键注册失败）。</summary>
    public void ShowWarning(string message)
    {
        if (_icon is null)
        {
            return;
        }

        try
        {
            _icon.ShowNotification(
                title: "DeskTools",
                message: message,
                icon: NotificationIcon.Warning,
                timeout: TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "显示托盘通知失败：{Message}", message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(CreateItem("显示 / 隐藏面板（Alt+X）", () => _panel?.ToggleVisibility()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateItem("清空面板", ClearPanel));
        menu.Items.Add(CreateItem("打开数据 / 设置目录", OpenSettingsFolder));
        menu.Items.Add(new Separator());

        // 这是退出的唯一入口（顶栏已不提供关闭按钮），因此措辞必须明确
        menu.Items.Add(CreateItem("退出 DeskTools", ExitApplication));

        return menu;
    }

    private static MenuItem CreateItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void ClearPanel()
    {
        if (_panel?.DataContext is PanelViewModel viewModel)
        {
            _panel.ShowPanel();
            viewModel.ClearAllCommand.Execute(null);
        }
    }

    private void OpenSettingsFolder()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskTools");

        try
        {
            Directory.CreateDirectory(directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "打开数据目录失败：{Directory}", directory);
        }
    }

    private void ExitApplication()
    {
        _logger.LogInformation("用户从托盘菜单退出。");

        // 先放行窗口的「关闭即收起」拦截，再走正常退出流程（会保存内容与窗口位置）
        _panel?.PrepareForShutdown();
        _app?.Shutdown();
    }

    /// <summary>从嵌入资源加载托盘图标，并缩放为托盘所需的 16px 尺寸。</summary>
    private static ImageSource? LoadTrayIcon()
    {
        try
        {
            var decoder = new IconBitmapDecoder(
                new Uri(IconResourceUri, UriKind.Absolute),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            // 选最接近托盘尺寸的一帧，避免系统缩放导致模糊
            var frame = decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - 16))
                .First();

            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
