using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskTools.App.ViewModels;
using DeskTools.App.Views.Units;
using DeskTools.Application.Abstractions;
using DeskTools.Application.Services;
using DeskTools.Application.Settings;
using DeskTools.Domain;
using DeskTools.Domain.Units;
using DeskTools.Infrastructure.Clipboard;
using DeskTools.Infrastructure.Windows;
using Microsoft.Extensions.Logging;

namespace DeskTools.App.Views;

/// <summary>
/// 面板窗口（无边框、置顶、可缩放）。
/// 视觉与布局全部在 XAML；本文件只承担三类无法用纯 XAML 表达的职责：
/// <list type="number">
/// <item>窗口级行为：默认定位、尺寸记忆、显示 / 隐藏与聚焦；</item>
/// <item>原始输入：拖拽落盘、修饰键点击、双击、编辑框按键；</item>
/// <item>Win32 交互：缩放热区、隐藏而非关闭。</item>
/// </list>
/// 所有业务操作都转交给 <see cref="PanelViewModel"/>，这里不写业务判断。
/// </summary>
public partial class PanelWindow : Window
{
    private const int WmNcHitTest = 0x0084;
    private const int WmExitSizeMove = 0x0232;
    private const int ResizeBorderThickness = 6;

    private readonly PanelViewModel _viewModel;
    private readonly IDisplayService _display;
    private readonly ISettingsStore _settings;
    private readonly ISystemLauncher _launcher;
    private readonly ILogger<PanelWindow> _logger;
    private readonly string _settingsDirectory;

    private bool _isHiddenByUser;

    /// <summary>创建面板窗口。</summary>
    public PanelWindow(
        PanelViewModel viewModel,
        IDisplayService display,
        ISettingsStore settings,
        ISystemLauncher launcher,
        ILogger<PanelWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeskTools");

        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        SourceInitialized += OnSourceInitialized;

        AllowDrop = true;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>面板当前是否可见。</summary>
    public bool IsPanelVisible => IsVisible;

    /// <summary>
    /// 切换面板可见性（全局热键的落点）。
    /// 显示时抢焦点，保证呼出后能立刻按 Ctrl+V。
    /// </summary>
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            HidePanel();
        }
        else
        {
            ShowPanel();
        }
    }

    /// <summary>显示面板并聚焦。</summary>
    public void ShowPanel()
    {
        _isHiddenByUser = false;

        if (!IsVisible)
        {
            Show();
        }

        Topmost = true;
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    /// <summary>隐藏面板（保留托盘图标；顶栏最小化与热键都走这里）。</summary>
    public void HidePanel()
    {
        _viewModel.DismissStatus();
        SavePlacement();
        _isHiddenByUser = true;
        Hide();
    }

    /// <summary>退出应用前持久化界面状态。</summary>
    public Task PersistAsync(CancellationToken cancellationToken = default)
        => SavePlacementCoreAsync(cancellationToken);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplyPlacement();
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "面板初始化失败。");
            _viewModel.ShowStatus("初始化失败，详见日志。");
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
        => ((HwndSource?)PresentationSource.FromVisual(this))?.AddHook(WndProc);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 顶栏关闭按钮 = 隐藏；真正退出由 TrayIcon / 显式 Shutdown 完成
        if (_isHiddenByUser || WpfApp.Current.ShutdownMode == ShutdownMode.OnExplicitShutdown)
        {
            return;
        }

        e.Cancel = true;
        HidePanel();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 已确认策略：失焦不自动隐藏（便于连续拖拽），仅记录位置
        SavePlacement();
    }

    // ————————————————————————— 位置与尺寸 —————————————————————————

    private void ApplyPlacement()
    {
        var placement = _settings.Current.Placement;

        Width = placement.Width;
        Height = placement.Height;

        var position = DisplayService.ResolvePosition(placement, new System.Windows.Size(Width, Height), _display);
        Left = position.X;
        Top = position.Y;

        _logger.LogDebug("面板位置 ({Left},{Top})，尺寸 {Width}×{Height}。", Left, Top, Width, Height);
    }

    private void SavePlacement()
    {
        if (!IsLoaded || WindowState != WindowState.Normal)
        {
            return;
        }

        _ = SavePlacementCoreAsync();
    }

    private async Task SavePlacementCoreAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var placement = new WindowPlacement
            {
                Left = Left,
                Top = Top,
                Width = ActualWidth > 0 ? ActualWidth : Width,
                Height = ActualHeight > 0 ? ActualHeight : Height,
                MonitorDeviceName = _display.GetMonitorDeviceNameFromCursor(),
            };

            var current = _settings.Current;
            if (Equals(current.Placement, placement))
            {
                return;
            }

            await _settings.SaveAsync(current with { Placement = placement }, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存窗口位置失败。");
        }
    }

    // ————————————————————————— Win32：缩放热区 —————————————————————————

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            // lParam 低 16 位为 x、高 16 位为 y（物理像素）
            var x = unchecked((short)(long)lParam);
            var y = unchecked((short)((long)lParam >> 16));
            var hit = HitTestResize(x, y);
            if (hit != 0)
            {
                handled = true;
                return new IntPtr(hit);
            }
        }

        if (msg == WmExitSizeMove)
        {
            SavePlacement();
        }

        return IntPtr.Zero;
    }

    /// <summary>判断屏幕坐标是否落在窗口边缘，返回 HT* 命中码（0 表示不在边缘）。</summary>
    private int HitTestResize(int screenX, int screenY)
    {
        if (WindowState != WindowState.Normal || ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize)
        {
            return 0;
        }

        var screenPoint = new Point(screenX, screenY);
        var local = PointFromScreen(screenPoint);
        var width = ActualWidth;
        var height = ActualHeight;
        var border = ResizeBorderThickness;

        var left = local.X <= border;
        var right = local.X >= width - border;
        var top = local.Y <= border;
        var bottom = local.Y >= height - border;

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => 13,   // HTTOPLEFT
            (_, true, true, _) => 14,   // HTTOPRIGHT
            (true, _, _, true) => 16,   // HTBOTTOMLEFT
            (_, true, _, true) => 17,   // HTBOTTOMRIGHT
            (true, _, _, _) => 10,      // HTLEFT
            (_, true, _, _) => 11,      // HTRIGHT
            (_, _, true, _) => 12,      // HTTOP
            (_, _, _, true) => 15,      // HTBOTTOM
            _ => 0,
        };
    }

    // ————————————————————————— 拖拽落盘 —————————————————————————

    private void OnDragOver(object sender, DragEventArgs e)
    {
        // 只做「能否接收」的探测，不做解析（解析成本高，且 DragOver 会被高频调用）
        e.Effects = e.Data is not null && DataObjectReader.TryReadPayload(e.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        try
        {
            var payload = DataObjectReader.TryReadPayload(e.Data);
            var content = DataObjectReader.ToRawContent(payload, UnitOrigin.DragDrop);
            if (content is null)
            {
                _viewModel.ShowStatus("拖入的内容暂不支持存放。");
                return;
            }

            await _viewModel.DropAsync(content).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理拖入内容失败。");
            _viewModel.ShowStatus("处理拖入内容失败，详见日志。");
        }
    }

    // ————————————————————————— 键盘与鼠标 —————————————————————————

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var editing = IsEditingText();

        switch (e.Key)
        {
            case Key.V when ctrl && !editing:
                e.Handled = true;
                _viewModel.PasteCommand.Execute(null);
                break;

            case Key.C when ctrl && !editing:
                e.Handled = true;
                _viewModel.CopySelectionCommand.Execute(null);
                break;

            case Key.Z when ctrl && !editing:
                e.Handled = true;
                _viewModel.UndoCommand.Execute(null);
                break;

            case Key.Delete when !editing:
                e.Handled = true;
                _viewModel.RemoveSelectionCommand.Execute(null);
                break;

            case Key.Escape:
                e.Handled = true;
                if (editing)
                {
                    CommitActiveEdit();
                    _viewModel.ClearSelection();
                }
                else
                {
                    HidePanel();
                }

                break;
        }
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (HitTestResizeFromEvent(e))
        {
            return;
        }

        var card = FindAncestor<UnitCard>(e.OriginalSource as DependencyObject);
        if (card?.DataContext is not UnitViewModel unit)
        {
            // 点击空白：提交编辑并清空选择
            CommitActiveEdit();
            _viewModel.ClearSelection();
            return;
        }

        if (card.IsEditing)
        {
            return;
        }

        CommitActiveEdit();

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        _viewModel.HandleUnitClick(unit, ctrl, shift);

        if (e.ClickCount == 2 && !ctrl && !shift)
        {
            _viewModel.ActivateUnitCommand.Execute(unit);

            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => FocusEditor(unit)));
        }
    }

    private bool HitTestResizeFromEvent(MouseButtonEventArgs e)
    {
        try
        {
            var screen = PointToScreen(e.GetPosition(this));
            var dpi = VisualTreeHelper.GetDpi(this);
            return HitTestResize((int)(screen.X * dpi.DpiScaleX), (int)(screen.Y * dpi.DpiScaleY)) != 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsEditingText() => Keyboard.FocusedElement is System.Windows.Controls.TextBox;

    /// <summary>把所有处于编辑态的卡片内容回写（失焦 / 点击他处即提交）。</summary>
    private void CommitActiveEdit()
    {
        foreach (var card in FindVisualChildren<UnitCard>(this))
        {
            if (!card.IsEditing || card.DataContext is not UnitViewModel unit)
            {
                continue;
            }

            var editor = FindVisualChild<System.Windows.Controls.TextBox>(card);
            _viewModel.CommitEdit(unit, editor?.Text ?? unit.Text ?? string.Empty);
        }
    }

    private void FocusEditor(UnitViewModel unit)
    {
        foreach (var card in FindVisualChildren<UnitCard>(this))
        {
            if (card.DataContext is not UnitViewModel candidate || candidate.Id != unit.Id)
            {
                continue;
            }

            var editor = FindVisualChild<System.Windows.Controls.TextBox>(card);
            if (editor is null)
            {
                return;
            }

            editor.Focus();
            editor.SelectAll();
            return;
        }
    }

    // ————————————————————————— 顶栏按钮 —————————————————————————

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => HidePanel();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _ = OpenSettingsFolderAsync();

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _logger.LogInformation("用户通过顶栏关闭按钮退出应用。");
        WpfApp.Current.Shutdown();
    }

    private async Task OpenSettingsFolderAsync()
    {
        // 设置界面安排在后续阶段；当前先打开设置文件所在目录，保证入口可用且不误导
        var ok = await _launcher.OpenPathAsync(_settingsDirectory).ConfigureAwait(true);
        _viewModel.ShowStatus(ok ? "已打开设置目录（设置界面在后续阶段提供）。" : "无法打开设置目录。");
    }

    // ————————————————————————— 视觉树辅助 —————————————————————————

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
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
