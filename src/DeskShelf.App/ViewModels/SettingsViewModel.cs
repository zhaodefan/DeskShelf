using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskShelf.App.Services;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Infrastructure.Theming;
using DeskShelf.Infrastructure.Windows;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App.ViewModels;

/// <summary>
/// 设置窗口的 ViewModel。
/// </summary>
/// <remarks>
/// 交互约定（已确认）：所有开关类设置<b>即时生效</b>，热键在点击「保存」时才正式生效
/// （注册失败会停留在本窗口并给出原因，避免用户以为改成功了其实没生效）；
/// 取消则把之前的设置整体回滚。
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private readonly IHotkeyService _hotkeys;
    private readonly AutoStartService _autoStartService;
    private readonly ShelfService _shelf;
    private readonly IThemeApplier _themeApplier;
    private readonly Func<Task> _openDataFolder;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>打开窗口时的设置快照，用于「取消」回滚。</summary>
    private ShelfSettings _original;

    [ObservableProperty]
    private HotkeyGesture _hotkey = HotkeyGesture.Default;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private AppTheme _theme = AppTheme.System;

    [ObservableProperty]
    private bool _showImageThumbnails = true;

    [ObservableProperty]
    private int _maxUnits = 500;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isHotkeyConflict;

    /// <summary>创建设置 ViewModel。</summary>
    public SettingsViewModel(
        ISettingsStore settings,
        IHotkeyService hotkeys,
        AutoStartService autoStartService,
        ShelfService shelf,
        IThemeApplier themeApplier,
        Func<Task> openDataFolder,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hotkeys = hotkeys ?? throw new ArgumentNullException(nameof(hotkeys));
        _autoStartService = autoStartService ?? throw new ArgumentNullException(nameof(autoStartService));
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _themeApplier = themeApplier ?? throw new ArgumentNullException(nameof(themeApplier));
        _openDataFolder = openDataFolder ?? throw new ArgumentNullException(nameof(openDataFolder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _original = settings.Current;
        LoadFrom(_original);
    }

    /// <summary>当前 unit 数量文案。</summary>
    public string UnitCountText => $"当前已存放 {_shelf.Count} 个 unit";

    /// <summary>可选主题项（用于绑定单选）。</summary>
    public IReadOnlyList<ThemeOption> ThemeOptions { get; } = new[]
    {
        new ThemeOption(AppTheme.System, "跟随系统"),
        new ThemeOption(AppTheme.Light, "浅色"),
        new ThemeOption(AppTheme.Dark, "深色"),
    };

    /// <summary>热键显示文本。</summary>
    public string HotkeyText => Hotkey.DisplayText;

    /// <summary>从当前设置载入界面。</summary>
    public void Reload()
    {
        _original = _settings.Current;
        LoadFrom(_original);
        StatusMessage = string.Empty;
        IsHotkeyConflict = false;
    }

    /// <summary>捕获到新的热键组合：先试注册，让用户在保存前就知道是否可用。</summary>
    /// <remarks>
    /// 注意：注册/注销必须<b>直接在 UI 线程</b>执行，不能包在 <c>Task.Run</c> 里 ——
    /// 热键依赖一个 HwndSource 消息窗口，而 Win32 要求 <c>RegisterHotKey</c> /
    /// <c>UnregisterHotKey</c> 与窗口所属线程一致，换线程会返回 1408/1409 之类的失败。
    /// </remarks>
    public Task SetHotkeyAsync(HotkeyGesture gesture)
    {
        Hotkey = gesture;
        OnPropertyChanged(nameof(HotkeyText));
        IsHotkeyConflict = false;
        StatusMessage = string.Empty;

        if (!gesture.IsValid)
        {
            IsHotkeyConflict = true;
            StatusMessage = "热键需要至少一个修饰键（Ctrl / Alt / Shift / Win）加一个主键。";
            return Task.CompletedTask;
        }

        // 先注销当前热键再试注册：否则 RegisterHotKey 会因为「同进程已注册过」而失败
        _hotkeys.Unregister();

        if (_hotkeys.TryRegister(gesture))
        {
            StatusMessage = $"已切换到 {gesture.DisplayText}。";
            return Task.CompletedTask;
        }

        // 试注册失败：恢复原热键，避免用户点「取消」后热键彻底失效
        _hotkeys.TryRegister(_original.Hotkey);

        // 同时把界面回退到原热键，避免显示一个实际没生效的组合
        IsHotkeyConflict = true;
        StatusMessage = $"{gesture.DisplayText} 已被其他程序占用，请换一个组合。";
        Hotkey = _original.Hotkey;
        OnPropertyChanged(nameof(HotkeyText));
        return Task.CompletedTask;
    }

    /// <summary>主题切换：立即预览（不等保存）。</summary>
    public void ApplyThemePreview() => _themeApplier.Preview(Theme switch
    {
        AppTheme.Light => true,
        AppTheme.Dark => false,
        _ => SystemThemeDetector.IsLightTheme(),
    });

    /// <summary>
    /// 供单选按钮绑定：当前主题是否等于给定值。
    /// 用方法而不用转换器，是为了让「单选按钮 ↔ 枚举」的映射只存在于一处。
    /// </summary>
    public bool IsThemeSelected(AppTheme candidate) => Theme == candidate;

    /// <summary>开机自启开关：立即写入注册表。</summary>
    public void ApplyAutoStart()
    {
        if (_autoStartService.SetEnabled(AutoStart))
        {
            StatusMessage = AutoStart ? "已设置开机自启。" : "已取消开机自启。";
            return;
        }

        AutoStart = _autoStartService.IsEnabled();
        StatusMessage = "开机自启设置失败（注册表写入被拒绝）。";
    }

    /// <summary>保存全部设置。</summary>
    public async Task<bool> SaveAsync()
    {
        if (IsHotkeyConflict)
        {
            StatusMessage = "热键不可用，请先改成其他组合。";
            return false;
        }

        var updated = _settings.Current with
        {
            Hotkey = Hotkey,
            AutoStart = AutoStart,
            Theme = Theme,
            ShowImageThumbnails = ShowImageThumbnails,
            MaxUnits = MaxUnits,
        };

        await _settings.SaveAsync(updated).ConfigureAwait(true);

        // 主题按新设置再应用一次（预览阶段可能只是临时切换）
        _themeApplier.ApplyFromSettings();

        _original = updated;
        StatusMessage = "设置已保存。";
        _logger.LogInformation("设置已保存：热键 {Hotkey}，主题 {Theme}，自启 {AutoStart}。",
            updated.Hotkey.DisplayText, updated.Theme, updated.AutoStart);
        return true;
    }

    /// <summary>取消：把设置回滚到打开窗口时的状态。</summary>
    public async Task CancelAsync()
    {
        await _settings.SaveAsync(_original).ConfigureAwait(true);
        _autoStartService.SetEnabled(_original.AutoStart);
        _themeApplier.ApplyFromSettings();
        _hotkeys.TryRegister(_original.Hotkey);
        LoadFrom(_original);
    }

    /// <summary>打开数据目录（面板外层的降级入口，设置窗口里给它一个正式位置）。</summary>
    public Task OpenDataFolderAsync() => _openDataFolder();

    /// <summary>清空面板内容。</summary>
    public void ClearShelf()
    {
        var result = _shelf.ClearAll();
        OnPropertyChanged(nameof(UnitCountText));
        StatusMessage = $"{result.Message}（此操作无法撤销）";
    }

    private void LoadFrom(ShelfSettings settings)
    {
        Hotkey = settings.Hotkey;
        AutoStart = settings.AutoStart;
        Theme = settings.Theme;
        ShowImageThumbnails = settings.ShowImageThumbnails;
        MaxUnits = settings.MaxUnits;
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(UnitCountText));
    }
}

/// <summary>主题单选项。</summary>
/// <param name="Value">主题枚举值。</param>
/// <param name="Label">显示名。</param>
public sealed record ThemeOption(AppTheme Value, string Label);
