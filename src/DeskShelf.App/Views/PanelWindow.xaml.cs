using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DeskShelf.App.ViewModels;
using DeskShelf.App.Views.Units;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Domain;
using DeskShelf.Domain.Units;
using DeskShelf.Infrastructure.Clipboard;
using DeskShelf.Infrastructure.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App.Views;

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
    private readonly IServiceProvider _services;
    private readonly ILogger<PanelWindow> _logger;

    private bool _isShuttingDown;

    /// <summary>DragOver 会被高频调用，用它保证「拖拽进入」日志每次拖拽只写一条。</summary>
    private bool _dragOverLogged;

    /// <summary>创建面板窗口。</summary>
    /// <remarks>
    /// 注入 <see cref="IServiceProvider"/> 而不是 <c>Func&lt;SettingsWindow&gt;</c>：
    /// 容器不认 <c>Func&lt;T&gt;</c>，会让 <c>ValidateOnBuild</c> 在启动阶段直接报错；
    /// 惰性解析设置窗口（单例）既避免启动即构造，也不需要额外注册工厂委托。
    /// </remarks>
    public PanelWindow(
        PanelViewModel viewModel,
        IDisplayService display,
        ISettingsStore settings,
        ISystemLauncher launcher,
        IServiceProvider services,
        ILogger<PanelWindow> logger)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        InitializeComponent();
        DataContext = viewModel;

        // 「新建文本」后立即把光标送进该卡片的编辑框（焦点操作属于视图职责）
        viewModel.UnitEditingRequested += OnUnitEditingRequested;

        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += OnDeactivated;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        PreviewMouseRightButtonDown += OnPreviewMouseRightButtonDown;
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
        // 先提交可能正在编辑的卡片：否则「输入后直接收起面板」会丢掉这段文字
        CommitActiveEdit();
        _viewModel.DismissStatus();
        SavePlacement();
        Hide();
    }

    /// <summary>
    /// 由托盘「退出」调用：放行 <see cref="Window.Close"/> 的取消逻辑，再执行关闭。
    /// </summary>
    public void PrepareForShutdown()
    {
        _isShuttingDown = true;
        CommitActiveEdit();
        SavePlacement();
    }

    /// <summary>退出应用前持久化界面状态。</summary>
    public Task PersistAsync(CancellationToken cancellationToken = default)
    {
        // 退出路径同样要兜住未提交的编辑（例如编辑中直接按托盘退出）
        CommitActiveEdit();
        return SavePlacementCoreAsync(cancellationToken);
    }

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
        // 顶栏已不再提供关闭按钮，"退出"的唯一正规路径是托盘右键菜单。
        // 因此除「应用正在退出」外的任何关闭请求（Esc、Alt+F4、系统菜单）都只收起面板，
        // 保证托盘图标与全局热键继续可用 —— 这正是「固定最小化」的语义。
        if (_isShuttingDown)
        {
            return;
        }

        e.Cancel = true;
        HidePanel();
        _logger.LogDebug("面板已收起（可从托盘图标或 {Gesture} 再次呼出）。", _settings.Current.Hotkey.DisplayText);
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
        var payload = e.Data is null ? null : DataObjectReader.TryReadPayload(e.Data);
        e.Effects = payload is not null ? DragDropEffects.Copy : DragDropEffects.None;

        // 诊断：拖拽是否真的到达了面板，以及携带的是哪种格式（排查「拖不动」的关键依据）
        if (payload is not null && !_dragOverLogged)
        {
            _dragOverLogged = true;
            _logger.LogInformation("拖拽已进入面板：{Summary}", Describe(payload));
        }

        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        _dragOverLogged = false;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragOverLogged = false;

        try
        {
            var payload = DataObjectReader.TryReadPayload(e.Data);
            _logger.LogInformation("收到拖拽落盘：{Summary}", payload is null ? "无法读取任何格式" : Describe(payload));

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

    /// <summary>把拖拽载荷概括成一行日志文本（含可用的全部格式，便于定位来源差异）。</summary>
    private static string Describe(DataObjectPayload payload)
    {
        var parts = new List<string>();
        if (payload.FilePaths is { Count: > 0 } paths)
        {
            var preview = paths.Count == 1 ? paths[0] : $"{paths[0]} 等 {paths.Count} 项";
            parts.Add($"Files=[{preview}]");
        }

        if (payload.ImageBytes is { Length: > 0 } image)
        {
            parts.Add($"Bitmap={image.Length}B");
        }

        if (payload.Text is { Length: > 0 } text)
        {
            var preview = text.Length > 80 ? text[..80] + "…" : text;
            parts.Add($"Text=[{preview.ReplaceLineEndings(" ")}]");
        }

        if (payload.Html is { Length: > 0 } html)
        {
            parts.Add($"Html={html.Length}B");
        }

        return parts.Count == 0 ? "（无可识别格式）" : string.Join(" / ", parts);
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

            // 退格键与 Delete 等价：选中后按一下就删，比去找 Delete 更顺手。
            // 必须被 !editing 拦住，否则在行内编辑框里按退格会连带删掉整张卡片。
            case Key.Back when !editing:
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

    /// <summary>
    /// 右键：先让被点的卡片成为选中项，再弹出操作菜单。
    /// </summary>
    /// <remarks>
    /// 菜单里的「复制」「移除」作用于**当前选择**，因此必须先把右键目标选上，
    /// 否则会出现「右键 A 却删了之前选中的 B」。
    /// 若右键目标已在多选集合内，则保留整个选择（用户通常想对整组操作）。
    /// </remarks>
    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var card = FindAncestor<UnitCard>(e.OriginalSource as DependencyObject);
        if (card?.DataContext is not UnitViewModel unit)
        {
            return;
        }

        // 编辑态下右键交给文本框自己的菜单（复制/粘贴文字）
        if (card.IsEditing)
        {
            return;
        }

        CommitActiveEdit();

        if (!unit.IsSelected)
        {
            _viewModel.ClearSelection();
            unit.SetSelected(true);
        }

        _logger.LogDebug("右键 unit {Id}。", unit.Id);
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

    /// <summary>「新建文本」等操作请求进入编辑：先提交其它正在编辑的卡片，再聚焦目标。</summary>
    private void OnUnitEditingRequested(object? sender, UnitViewModel unit)
    {
        unit.BeginEdit();

        // 新卡片刚加入列表，容器可能要下一轮布局才生成，因此延后一拍再找编辑框
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                FocusEditor(unit);

                // 新 unit 在末尾：滚到底部让它可见，否则用户看不到刚建的空卡片
                UnitList.UpdateLayout();
                var card = FindVisualChildren<UnitCard>(this)
                    .FirstOrDefault(c => ReferenceEquals(c.DataContext, unit));
                card?.BringIntoView();
            }));
    }

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

            // Focus() 只申请逻辑焦点，Keyboard.Focus 才真正把键盘输入引进来
            editor.Focus();
            Keyboard.Focus(editor);
            editor.SelectAll();
            return;
        }
    }

    // ————————————————————————— 顶栏按钮 —————————————————————————

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => HidePanel();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>
    /// 打开设置窗口并返回它（托盘「检查更新」也走这里）。
    /// </summary>
    /// <returns>已显示的设置窗口；打开失败时返回 <c>null</c>。</returns>
    public SettingsWindow? OpenSettings()
    {
        try
        {
            // 每次都用新窗口实例：WPF 的 Window 一旦 Close 就不能再 Show，
            // 复用单例会在「关闭后再次点击设置」时抛 InvalidOperationException。
            var settings = _services.GetRequiredService<SettingsWindow>();

            // ViewModel 是单例：重新载入快照，否则「取消」会回滚到上一次打开时的状态
            settings.ReloadFromSettings();

            settings.Owner = this;
            settings.Show();
            settings.Activate();
            _logger.LogDebug("设置窗口已打开。");
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开设置窗口失败。");
            _viewModel.ShowStatus("打开设置窗口失败，详见日志。");
            return null;
        }
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
