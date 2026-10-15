using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskShelf.App.Services;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Application.UseCases;
using DeskShelf.Domain;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App.ViewModels;

/// <summary>
/// 面板主 ViewModel：把 <see cref="ShelfService"/> 的用例能力
/// 暴露为可直接绑定的集合与命令。视图层不做任何业务判断。
/// </summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly ShelfService _shelf;
    private readonly ISettingsStore _settings;
    private readonly ImageBytesCache _imageCache;
    private readonly ThumbnailLoader? _thumbnailLoader;
    private readonly SampleDataSeeder? _seeder;
    private readonly ThumbnailLoadScheduler? _thumbnails;
    private readonly ILogger<PanelViewModel> _logger;

    /// <summary>区间选择（Shift+点击）的锚点。</summary>
    private UnitViewModel? _selectionAnchor;

    /// <summary>消息条的自动消失计时器。</summary>
    private CancellationTokenSource? _messageDismiss;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusVisible;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isPinned = true;

    /// <summary>创建面板 ViewModel。</summary>
    public PanelViewModel(
        ShelfService shelf,
        ISettingsStore settings,
        ImageBytesCache imageCache,
        ILogger<PanelViewModel> logger,
        ThumbnailLoader? thumbnailLoader = null,
        SampleDataSeeder? seeder = null,
        ThumbnailLoadScheduler? thumbnails = null)
    {
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _imageCache = imageCache ?? throw new ArgumentNullException(nameof(imageCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thumbnailLoader = thumbnailLoader;
        _seeder = seeder;
        _thumbnails = thumbnails;

        _shelf.Changed += OnShelfChanged;
    }

    /// <summary>请求视图让某个 unit 进入编辑态并聚焦（焦点操作属于视图职责）。</summary>
    public event EventHandler<UnitViewModel>? UnitEditingRequested;

    /// <summary>面板中的 unit 卡片集合（与领域集合保持同步）。</summary>
    public ObservableCollection<UnitViewModel> Units { get; } = new();

    /// <summary>空态提示是否可见。</summary>
    public bool IsEmpty => Units.Count == 0;

    /// <summary>unit 数量文案。</summary>
    public string CountText => Units.Count == 0 ? "空" : $"{Units.Count} 项";

    /// <summary>当前是否可撤销删除。</summary>
    public bool CanUndo => _shelf.CanUndo;

    /// <summary>启动时加载持久化内容。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsPinned = true;
        await _shelf.LoadAsync(cancellationToken).ConfigureAwait(true);

        // 设计走查样例（仅 DeskShelf_SEED_SAMPLE=1 时生效）
        if (Services.SampleDataSeeder.IsEnabled && _seeder is not null)
        {
            await _seeder.SeedAsync(cancellationToken).ConfigureAwait(true);
        }

        SyncUnits();

        // 启动时平台上的图片 unit 需要补齐缩略图
        _thumbnails?.Request(Units);
    }

    // —————————————————————————————— 命令 ——————————————————————————————

    /// <summary>Ctrl+V：把剪贴板内容存为新的 unit。</summary>
    [RelayCommand]
    private async Task PasteAsync()
    {
        await RunAsync(async () =>
        {
            var result = await _shelf.IngestFromClipboardAsync().ConfigureAwait(true);
            ShowStatus(result.Message);
        }).ConfigureAwait(true);
    }

    /// <summary>拖拽落盘：把外部拖入的内容存为 unit。</summary>
    public async Task DropAsync(RawContent content, int? insertIndex = null)
    {
        await RunAsync(async () =>
        {
            var result = await _shelf.IngestAsync(content, insertIndex).ConfigureAwait(true);
            ShowStatus(result.Message);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// 新建一个空白的文本 unit，并请求视图立即进入编辑态。
    /// </summary>
    /// <remarks>
    /// 空文本 unit 本身没有价值，所以「创建 + 立刻可输入」是一体的：
    /// 这里只负责创建与选中，实际聚焦由视图响应 <see cref="UnitEditingRequested"/> 完成
    /// （焦点操作属于视图职责，ViewModel 不碰 Keyboard）。
    /// </remarks>
    [RelayCommand]
    private void AddTextUnit()
    {
        var result = _shelf.CreateTextUnit(out var unitId);
        if (!result.Success || unitId is null)
        {
            ShowStatus(result.Message);
            return;
        }

        var created = Units.FirstOrDefault(u => u.Id == unitId.Value);
        if (created is null)
        {
            return;
        }

        ClearSelection();
        created.SetSelected(true);
        _selectionAnchor = created;
        ShowStatus("已新建文本 unit，直接输入即可；点击其他位置完成编辑。");
        UnitEditingRequested?.Invoke(this, created);
    }

    /// <summary>Ctrl+C：复制选中的 unit。</summary>
    [RelayCommand]
    private async Task CopySelectionAsync()
    {
        var selected = SelectedUnits();
        if (selected.Count == 0)
        {
            ShowStatus("请先选中要复制的内容。");
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _shelf.CopyAsync(selected.Select(vm => vm.Model).ToList()).ConfigureAwait(true);
            ShowStatus(result.Message);
        }).ConfigureAwait(true);
    }

    /// <summary>复制指定 unit（卡片悬停小标）。</summary>
    [RelayCommand]
    private async Task CopyUnitAsync(UnitViewModel? unit)
    {
        if (unit is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var result = await _shelf.CopyAsync(new[] { unit.Model }).ConfigureAwait(true);
            ShowStatus(result.Message);
        }).ConfigureAwait(true);
    }

    /// <summary>Delete：移除选中的 unit。</summary>
    [RelayCommand]
    private void RemoveSelection()
    {
        var selected = SelectedUnits();
        if (selected.Count == 0)
        {
            return;
        }

        var result = _shelf.Remove(selected.Select(vm => vm.Id).ToList());
        ShowStatus(result.Message, withUndoHint: result.CanUndo);
    }

    /// <summary>移除指定 unit（卡片悬停小标）。</summary>
    [RelayCommand]
    private void RemoveUnit(UnitViewModel? unit)
    {
        if (unit is null)
        {
            return;
        }

        var result = _shelf.Remove(new[] { unit.Id });
        ShowStatus(result.Message, withUndoHint: result.CanUndo);
    }

    /// <summary>Ctrl+Z：撤销最近一次删除。</summary>
    [RelayCommand]
    private void Undo()
    {
        var result = _shelf.Undo();
        ShowStatus(result.Message);
    }

    /// <summary>清空全部内容。</summary>
    [RelayCommand]
    private void ClearAll()
    {
        var result = _shelf.ClearAll();
        ShowStatus(result.Message, withUndoHint: result.CanUndo);
    }

    /// <summary>双击打开：文本进入编辑、图片用看图程序、文件夹用资源管理器。</summary>
    [RelayCommand]
    private async Task ActivateUnitAsync(UnitViewModel? unit)
    {
        if (unit is null)
        {
            return;
        }

        if (unit.Capabilities.CanEditText)
        {
            unit.BeginEdit();
            return;
        }

        await _shelf.OpenAsync(unit.Model).ConfigureAwait(true);
    }

    /// <summary>在资源管理器中定位。</summary>
    [RelayCommand]
    private async Task RevealUnitAsync(UnitViewModel? unit)
    {
        if (unit is null)
        {
            return;
        }

        var ok = await _shelf.RevealAsync(unit.Model).ConfigureAwait(true);
        if (!ok)
        {
            ShowStatus("无法在资源管理器中定位该项。");
        }
    }

    /// <summary>提交行内编辑。</summary>
    public void CommitEdit(UnitViewModel unit, string text)
    {
        unit.EndEdit();
        unit.CommitText(text);
    }

    /// <summary>点击空白区域：清空选择。</summary>
    public void ClearSelection()
    {
        foreach (var unit in Units)
        {
            unit.SetSelected(false);
        }

        _selectionAnchor = null;
    }

    /// <summary>处理卡片点击（支持 Ctrl 多选与 Shift 区间选择）。</summary>
    public void HandleUnitClick(UnitViewModel unit, bool ctrl, bool shift)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (shift && _selectionAnchor is not null)
        {
            var start = Units.IndexOf(_selectionAnchor);
            var end = Units.IndexOf(unit);
            if (start >= 0 && end >= 0)
            {
                if (start > end)
                {
                    (start, end) = (end, start);
                }

                for (var i = 0; i < Units.Count; i++)
                {
                    Units[i].SetSelected(i >= start && i <= end);
                }

                return;
            }
        }

        if (ctrl)
        {
            unit.ToggleSelection();
            _selectionAnchor = unit;
            return;
        }

        ClearSelection();
        unit.SetSelected(true);
        _selectionAnchor = unit;
    }

    /// <summary>显示一条消息条（默认 4 秒后自动消失，带撤销按钮时延长到 6 秒）。</summary>
    public void ShowStatus(string message, bool withUndoHint = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusMessage = message;
        IsStatusVisible = true;
        OnPropertyChanged(nameof(CanUndo));

        _messageDismiss?.Cancel();
        _messageDismiss?.Dispose();
        _messageDismiss = new CancellationTokenSource();
        var token = _messageDismiss.Token;
        var delay = withUndoHint ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(4);

        _ = Task.Delay(delay, token).ContinueWith(
            _ => IsStatusVisible = false,
            token,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>隐藏消息条。</summary>
    public void DismissStatus()
    {
        IsStatusVisible = false;
        _messageDismiss?.Cancel();
    }

    private List<UnitViewModel> SelectedUnits() => Units.Where(u => u.IsSelected).ToList();

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "面板操作失败。");
            ShowStatus("操作失败，详见日志。");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>依据领域集合重建 ViewModel 集合（保持已有实例，避免图片重复解码）。</summary>
    private void SyncUnits()
    {
        var existing = Units.ToDictionary(vm => vm.Id);
        var desired = _shelf.Shelf.Units.ToList();

        // 移除已不存在的
        for (var i = Units.Count - 1; i >= 0; i--)
        {
            if (!desired.Any(u => u.Id == Units[i].Id))
            {
                Units.RemoveAt(i);
            }
        }

        // 按领域顺序对齐，并补充新增项
        for (var i = 0; i < desired.Count; i++)
        {
            var model = desired[i];
            if (!existing.TryGetValue(model.Id, out var vm))
            {
                vm = new UnitViewModel(model, _shelf, _imageCache, _thumbnailLoader);
            }

            var currentIndex = Units.IndexOf(vm);
            if (currentIndex < 0)
            {
                Units.Insert(i, vm);
            }
            else if (currentIndex != i)
            {
                Units.Move(currentIndex, i);
            }
        }
    }

    private void OnShelfChanged(object? sender, ShelfChangedEventArgs e)
    {
        SyncUnits();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(CanUndo));

        if (e.Kind == ShelfChangeKind.Cleared)
        {
            _thumbnails?.Reset();
        }

        // 新增的图片卡片需要异步补齐缩略图
        _thumbnails?.Request(Units);
    }
}
