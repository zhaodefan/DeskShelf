using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskShelf.App.ViewModels;
using DeskShelf.Application.Settings;

namespace DeskShelf.App.Views;

/// <summary>
/// 设置窗口。视觉在 XAML，本文件只处理无法用声明式表达的部分：
/// 热键录入事件、单选按钮 ↔ 枚举的映射、数字输入过滤、保存/取消的编排、Esc 关闭。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>创建设置窗口。</summary>
    public SettingsWindow(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = viewModel;

        // 在线升级结束时由 ViewModel 请求关闭应用（保持「表现层负责进程生命周期」的边界）
        _viewModel.ShutdownApplicationAsync = ShutdownApplicationAsync;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>结束应用（在线升级完成后的重启路径）。</summary>
    private static Task ShutdownApplicationAsync()
    {
        // 后台任务里调用，需切回 UI 线程再关应用
        var app = WpfApp.Current;
        var dispatcher = app?.Dispatcher;
        if (app is null || dispatcher is null)
        {
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(app.Shutdown).Task;
    }

    /// <summary>
    /// 从当前设置重新载入界面。
    /// ViewModel 是单例（用于跨窗口存活），因此每次打开窗口都必须调用本方法刷新快照，
    /// 否则「取消」会回滚到上一次打开窗口时的设置。
    /// </summary>
    public void ReloadFromSettings()
    {
        _viewModel.Reload();
        SyncThemeSelection();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncThemeSelection();

        // 面板在最上层，设置窗口需要跟随，否则会被面板挡住
        Topmost = Owner?.Topmost ?? true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        // 热键录入过程中按 Esc 由录入框自行处理（放弃录入），这里不关窗口
        if (HotkeyInput.IsCapturing)
        {
            return;
        }

        e.Handled = true;
        Close();
    }

    private void OnHotkeyCaptured(object? sender, HotkeyGesture gesture)
        => _ = _viewModel.SetHotkeyAsync(gesture);

    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
        => _ = _viewModel.SetHotkeyAsync(HotkeyGesture.Default);

    private void OnThemeOptionClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: AppTheme theme })
        {
            _viewModel.Theme = theme;
            _viewModel.ApplyThemePreview();
        }
    }

    private void OnThumbnailToggled(object sender, RoutedEventArgs e)
    {
        // 勾选事件由框架在状态变化后触发，直接读控件状态最可靠
        if (sender is CheckBox box)
        {
            _viewModel.ShowImageThumbnails = box.IsChecked == true;
        }
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box)
        {
            return;
        }

        _viewModel.AutoStart = box.IsChecked == true;
        _viewModel.ApplyAutoStart();

        // 写注册表失败时 ViewModel 会把状态改回去，这里同步回控件
        if (box.IsChecked != _viewModel.AutoStart)
        {
            box.IsChecked = _viewModel.AutoStart;
        }
    }

    /// <summary>数量上限只允许数字，避免用户输入非法字符后才发现。</summary>
    private void OnMaxUnitsPreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(char.IsAsciiDigit);

    /// <summary>离开输入框时把文本提交给 ViewModel（非法输入回退到当前值）。</summary>
    private void OnMaxUnitsLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        if (int.TryParse(box.Text, out var value))
        {
            _viewModel.MaxUnits = value;
        }

        // 回显规范化后的值（超出范围会被 ViewModel 收敛）
        box.Text = _viewModel.MaxUnits.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
        => _ = _viewModel.OpenDataFolderAsync();

    // ————————————————————————— 更新 —————————————————————————

    private void OnCheckUpdateClick(object sender, RoutedEventArgs e)
        => _ = _viewModel.CheckForUpdatesAsync();

    private void OnOpenReleasesClick(object sender, RoutedEventArgs e)
        => _ = _viewModel.OpenReleasesPageAsync();

    private async void OnInstallUpdateClick(object sender, RoutedEventArgs e)
    {
        // 先把用户对设置的改动落盘，再开始升级 —— 否则重启后设置会丢失
        await _viewModel.SaveAsync().ConfigureAwait(true);

        if (!_viewModel.IsInstalledBuild)
        {
            // 便携版无法自动替换正在运行的文件：只下载并打开所在目录
            await _viewModel.InstallUpdateAsync().ConfigureAwait(true);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            "将下载新版本并在后台静默安装。\n\n安装期间本程序会自动退出，安装完成后自动重新启动（约 1 分钟）。\n\n现在开始吗？",
            "在线更新",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.OK);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        await _viewModel.InstallUpdateAsync().ConfigureAwait(true);
    }

    private void OnClearShelfClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            this,
            "确定要清空面板中的全部内容吗？（面板内的清空按钮可撤销，这里的清空无法撤销）",
            "桌面置物架",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.OK)
        {
            _viewModel.ClearShelf();
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (await _viewModel.SaveAsync().ConfigureAwait(true))
        {
            Close();
        }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.CancelAsync().ConfigureAwait(true);
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>把枚举状态同步到单选按钮（ItemsControl 生成的项无法直接双向绑定枚举）。</summary>
    private void SyncThemeSelection()
    {
        foreach (var button in FindVisualChildren<RadioButton>(this))
        {
            if (button.Tag is AppTheme theme)
            {
                button.IsChecked = _viewModel.IsThemeSelected(theme);
            }
        }

        ThumbnailToggle.IsChecked = _viewModel.ShowImageThumbnails;
        AutoStartToggle.IsChecked = _viewModel.AutoStart;
        MaxUnitsBox.Text = _viewModel.MaxUnits.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
