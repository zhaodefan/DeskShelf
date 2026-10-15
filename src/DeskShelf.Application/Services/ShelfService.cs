using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Catalog;
using DeskShelf.Application.Settings;
using DeskShelf.Application.Undo;
using DeskShelf.Application.UseCases;
using DeskShelf.Domain;
using DeskShelf.Domain.Shelf;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Application.Services;

/// <summary>
/// 置物架的<b>唯一逻辑入口</b>。表现层只与本类交互，不直接操作
/// <see cref="Shelf"/> 聚合，也不直接触碰剪贴板 / 文件系统 / 系统启动器。
/// </summary>
/// <remarks>
/// 线程模型约定：本类的方法可在任意线程调用；对 <see cref="Shelf"/> 集合的修改
/// 由调用方保证在 UI 线程上（表现层通过 <see cref="IUiDispatcher"/> 投递），
/// 以避免绑定到 ObservableCollection 的视图收到跨线程变更通知。
/// </remarks>
public sealed class ShelfService
{
    private readonly Shelf _shelf;
    private readonly IUnitContentFactory _contentFactory;
    private readonly IClipboardService _clipboard;
    private readonly ISystemLauncher _launcher;
    private readonly IShelfStore _store;
    private readonly ISettingsStore _settingsStore;
    private readonly IImageStore _imageStore;
    private readonly UndoStack _undoStack;
    private readonly ILogger<ShelfService> _logger;

    /// <summary>构造置物架服务。</summary>
    public ShelfService(
        Shelf shelf,
        IUnitContentFactory contentFactory,
        IClipboardService clipboard,
        ISystemLauncher launcher,
        IShelfStore store,
        ISettingsStore settingsStore,
        IImageStore imageStore,
        UndoStack undoStack,
        ILogger<ShelfService> logger)
    {
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _contentFactory = contentFactory ?? throw new ArgumentNullException(nameof(contentFactory));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _undoStack = undoStack ?? throw new ArgumentNullException(nameof(undoStack));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>置物架内容发生任何变化后触发（供表现层触发防抖保存与状态刷新）。</summary>
    public event EventHandler<ShelfChangedEventArgs>? Changed;

    /// <summary>底层聚合（只读暴露给表现层用于绑定）。</summary>
    public Shelf Shelf => _shelf;

    /// <summary>当前 unit 数量。</summary>
    public int Count => _shelf.Count;

    /// <summary>是否可撤销删除。</summary>
    public bool CanUndo => _undoStack.CanUndo;

    /// <summary>最近的撤销提示文案。</summary>
    public string? UndoHint => _undoStack.PeekDescription;

    /// <summary>启动时从存储加载；文件缺失或损坏时返回 0 并保持空面板。</summary>
    public async Task<int> LoadAsync(CancellationToken cancellationToken = default)
    {
        var units = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);
        _shelf.ReplaceAll(units);
        _undoStack.Clear();
        _logger.LogInformation("已加载 {Count} 个 unit。", _shelf.Count);
        RaiseChanged(ShelfChangeKind.Loaded);
        return _shelf.Count;
    }

    /// <summary>保存当前内容到存储。</summary>
    public Task SaveAsync(CancellationToken cancellationToken = default)
        => _store.SaveAsync(_shelf.Units.ToList(), cancellationToken);

    /// <summary>从剪贴板摄取内容（面板内 Ctrl+V）。</summary>
    public async Task<IngestResult> IngestFromClipboardAsync(CancellationToken cancellationToken = default)
    {
        RawContent? content;
        try
        {
            content = await _clipboard.TryReadAsync(cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取剪贴板失败。");
            return IngestResult.Nothing("读取剪贴板失败，请稍后重试。");
        }

        if (content is null)
        {
            return IngestResult.Nothing("剪贴板里没有可存放的内容。");
        }

        return await IngestAsync(content, insertIndex: null, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// 摄取归一化内容。粘贴与拖拽共用本方法，<paramref name="insertIndex"/> 为 <c>null</c> 时追加到末尾。
    /// </summary>
    public async Task<IngestResult> IngestAsync(
        RawContent content,
        int? insertIndex = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var limit = _settingsStore.Current.MaxUnits;
        if (_shelf.Count >= limit)
        {
            return IngestResult.Nothing($"面板已满（上限 {limit} 个），请先移除一些内容。");
        }

        UnitCreationResult creation;
        try
        {
            creation = await _contentFactory.CreateAsync(content, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "内容转换失败：{Kind}", content.Kind);
            return IngestResult.Nothing("内容转换失败，已跳过。");
        }

        if (!creation.HasAny)
        {
            return IngestResult.Nothing(creation.WarningText ?? "该内容暂不支持存放。");
        }

        var capacity = limit - _shelf.Count;
        var accepted = creation.Units.Count > capacity
            ? creation.Units.Take(capacity).ToList()
            : creation.Units;

        if (insertIndex is { } index)
        {
            var offset = 0;
            foreach (var unit in accepted)
            {
                _shelf.Insert(index + offset, unit);
                offset++;
            }
        }
        else
        {
            _shelf.AppendRange(accepted);
        }

        var ids = accepted.Select(u => u.Id).ToList();
        RaiseChanged(ShelfChangeKind.Added, ids);

        var message = BuildIngestMessage(accepted, creation.WarningText, creation.Units.Count - accepted.Count);
        _logger.LogInformation("新增 {Count} 个 unit（来源 {Kind}）。", accepted.Count, content.Kind);
        return IngestResult.Added(ids, message);
    }

    /// <summary>
    /// 复制选中的 unit 到剪贴板。
    /// 多选语义（已确认）：全文本拼接；全文件夹写文件列表；混合类型只复制最靠前的一项并提示。
    /// </summary>
    public async Task<CopyResult> CopyAsync(IReadOnlyList<ShelfUnit> units, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count == 0)
        {
            return CopyResult.Failed("请先选中要复制的内容。");
        }

        var copyable = units.Where(u => u.Capabilities.CanCopyToClipboard).ToList();
        if (copyable.Count == 0)
        {
            return CopyResult.Failed("选中的内容不支持复制到剪贴板。");
        }

        RawContent payload;
        string message;

        if (copyable.Count != units.Count)
        {
            var skipped = units.Count - copyable.Count;
            payload = await ToContentAsync(copyable[0], cancellationToken).ConfigureAwait(true);
            message = $"已复制 1 项；{skipped} 项不支持复制，已跳过。";
        }
        else if (copyable.All(u => u.Kind == UnitKind.Text))
        {
            var joined = string.Join("\n", copyable.Select(u => ((TextUnit)u).Text));
            payload = RawContent.FromText(joined);
            message = copyable.Count == 1 ? "已复制文本。" : $"已复制 {copyable.Count} 段文本（合并为一段）。";
        }
        else if (copyable.All(u => u.Kind == UnitKind.Folder))
        {
            var paths = copyable.Select(u => ((FolderUnit)u).Path).ToList();
            payload = RawContent.FromPaths(paths);
            message = copyable.Count == 1 ? "已复制路径。" : $"已复制 {copyable.Count} 个路径。";
        }
        else if (copyable.Count == 1)
        {
            payload = await ToContentAsync(copyable[0], cancellationToken).ConfigureAwait(true);
            message = copyable[0].Kind == UnitKind.Image ? "已复制图片。" : "已复制内容。";
        }
        else
        {
            payload = await ToContentAsync(copyable[0], cancellationToken).ConfigureAwait(true);
            message = $"已复制 1 项；{copyable.Count - 1} 项因类型不同未复制（混合类型暂不支持批量复制）。";
        }

        try
        {
            await _clipboard.WriteAsync(payload, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入剪贴板失败。");
            return CopyResult.Failed("写入剪贴板失败，可能被其他程序占用，请重试。");
        }

        return CopyResult.Ok(message);
    }

    /// <summary>移除指定 unit（可撤销）。</summary>
    public MutationResult Remove(IReadOnlyList<UnitId> ids, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return MutationResult.NoChange("没有可移除的内容。");
        }

        var snapshot = _shelf.RemoveMany(ids);
        if (snapshot.Count == 0)
        {
            return MutationResult.NoChange("没有可移除的内容。");
        }

        var action = new RemoveUnitsAction(_shelf, snapshot, description ?? $"已移除 {snapshot.Count} 项");
        _undoStack.Record(action);
        RaiseChanged(ShelfChangeKind.Removed, snapshot.Select(s => s.Unit.Id).ToList());

        _logger.LogInformation("移除 {Count} 个 unit。", snapshot.Count);
        return new MutationResult(snapshot.Count, CanUndo: true, action.Description);
    }

    /// <summary>清空全部 unit（可撤销）。</summary>
    public MutationResult ClearAll()
    {
        if (_shelf.Count == 0)
        {
            return MutationResult.NoChange("面板已经是空的。");
        }

        var snapshot = _shelf.ClearAll();
        var indexed = snapshot.Select((unit, index) => (Index: index, Unit: unit)).ToList();
        var action = new RemoveUnitsAction(_shelf, indexed, $"已清空 {snapshot.Count} 项");
        _undoStack.Record(action);
        RaiseChanged(ShelfChangeKind.Cleared);

        _logger.LogInformation("清空 {Count} 个 unit。", snapshot.Count);
        return new MutationResult(snapshot.Count, CanUndo: true, action.Description);
    }

    /// <summary>撤销最近一次删除；无可撤销内容时返回 <c>false</c>。</summary>
    public MutationResult Undo()
    {
        var action = _undoStack.Undo();
        if (action is null)
        {
            return MutationResult.NoChange("没有可撤销的操作。");
        }

        RaiseChanged(ShelfChangeKind.Restored);
        _logger.LogInformation("撤销：{Description}", action.Description);
        return new MutationResult(0, CanUndo: CanUndo, $"已撤销「{action.Description}」");
    }

    /// <summary>
    /// 新建一个空白文本 unit（供面板「新建文本」入口使用）。
    /// </summary>
    /// <param name="unitId">创建成功时输出新 unit 的标识。</param>
    /// <returns>操作结果；面板已满时失败并给出原因。</returns>
    /// <remarks>
    /// 刻意允许空文本：这是「先建一个空位、马上打字」的交互，与「粘贴空内容」不同 ——
    /// 后者才应该被拒绝。空 unit 若被用户放弃（未输入任何内容就失焦）会留在面板上，
    /// 属于用户可见的结果，可由用户自行删除。
    /// </remarks>
    public IngestResult CreateTextUnit(out UnitId? unitId)
    {
        unitId = null;

        var limit = _settingsStore.Current.MaxUnits;
        if (_shelf.Count >= limit)
        {
            return IngestResult.Nothing($"面板已满（上限 {limit} 个），请先移除一些内容。");
        }

        var unit = new TextUnit(string.Empty, UnitId.New(), UnitOrigin.NewUnitButton, DateTimeOffset.UtcNow);
        _shelf.Append(unit);
        unitId = unit.Id;
        RaiseChanged(ShelfChangeKind.Added, new[] { unit.Id });

        _logger.LogInformation("新建空白文本 unit。");
        return IngestResult.Added(new[] { unit.Id }, "已新建文本 unit。");
    }

    /// <summary>编辑文本 unit（行内编辑提交）。内容未变化时返回 <c>false</c>。</summary>
    public bool UpdateText(UnitId id, string text)
    {
        if (_shelf.Find(id) is not TextUnit unit)
        {
            return false;
        }

        var normalized = TextUnit.Normalize(text);
        if (string.Equals(unit.Text, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        unit.UpdateText(normalized);
        RaiseChanged(ShelfChangeKind.Updated, new[] { id });
        return true;
    }

    /// <summary>把 unit 移动到新下标（拖拽排序）。</summary>
    public bool Move(UnitId id, int newIndex)
    {
        if (!_shelf.Move(id, newIndex))
        {
            return false;
        }

        RaiseChanged(ShelfChangeKind.Moved, new[] { id });
        return true;
    }

    /// <summary>执行 unit 的默认打开动作（双击）。</summary>
    public Task<bool> OpenAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _launcher.OpenAsync(unit, cancellationToken);
    }

    /// <summary>在资源管理器中定位 unit。</summary>
    public Task<bool> RevealAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return _launcher.RevealInExplorerAsync(unit, cancellationToken);
    }

    private async Task<RawContent> ToContentAsync(ShelfUnit unit, CancellationToken cancellationToken)
    {
        switch (unit)
        {
            case TextUnit text:
                return RawContent.FromText(text.Text);
            case FolderUnit folder:
                return RawContent.FromPaths(new[] { folder.Path });
            case ImageUnit image:
                // 图片必须回读原始字节才能真正复制到剪贴板；读取失败时降级为抠出 ImageUnit 的键值
                var bytes = await _imageStore.ReadAsync(image.ImageKey, cancellationToken).ConfigureAwait(true);
                return bytes is null or { Length: 0 }
                    ? RawContent.FromText(image.ContentSummary)
                    : RawContent.FromImage(bytes, image.Format);
            default:
                // 未知类型（未来扩展）的兜底：复制其内容摘要，保证 Ctrl+C 不静默失效
                return RawContent.FromText(unit.ContentSummary);
        }
    }

    private static string BuildIngestMessage(IReadOnlyList<ShelfUnit> accepted, string? unsupportedReason, int skippedForCapacity)
    {
        if (unsupportedReason is not null && accepted.Count == 0)
        {
            return unsupportedReason;
        }

        var summary = accepted.Count == 1
            ? $"已存放 1 个{UnitCatalog.Get(accepted[0].Kind).DisplayName} unit。"
            : $"已存放 {accepted.Count} 个 unit。";

        if (unsupportedReason is not null)
        {
            return $"{summary}{unsupportedReason}";
        }

        return skippedForCapacity > 0
            ? $"{summary}另有 {skippedForCapacity} 项因超出上限被忽略。"
            : summary;
    }

    private void RaiseChanged(ShelfChangeKind kind, IReadOnlyList<UnitId>? affected = null)
        => Changed?.Invoke(this, new ShelfChangedEventArgs(kind, affected ?? Array.Empty<UnitId>()));

    /// <summary>删除类操作（可撤销）。首次执行由 <see cref="ShelfService"/> 直接完成。</summary>
    private sealed class RemoveUnitsAction : IUndoableAction
    {
        private readonly Shelf _shelf;
        private readonly IReadOnlyList<(int Index, ShelfUnit Unit)> _snapshot;

        public RemoveUnitsAction(Shelf shelf, IReadOnlyList<(int Index, ShelfUnit Unit)> snapshot, string description)
        {
            _shelf = shelf;
            _snapshot = snapshot;
            Description = description;
        }

        public string Description { get; }

        public void Apply()
        {
            foreach (var (_, unit) in _snapshot.OrderByDescending(s => s.Index))
            {
                _shelf.Remove(unit.Id);
            }
        }

        public void Revert() => _shelf.Restore(_snapshot);
    }
}

/// <summary>置物架变化类型。</summary>
public enum ShelfChangeKind
{
    /// <summary>启动加载完成。</summary>
    Loaded = 0,

    /// <summary>新增 unit。</summary>
    Added = 1,

    /// <summary>移除 unit。</summary>
    Removed = 2,

    /// <summary>清空。</summary>
    Cleared = 3,

    /// <summary>撤销恢复。</summary>
    Restored = 4,

    /// <summary>内容更新（文本编辑）。</summary>
    Updated = 5,

    /// <summary>顺序变化。</summary>
    Moved = 6,
}

/// <summary>置物架变化事件参数。</summary>
/// <param name="Kind">变化类型。</param>
/// <param name="AffectedIds">受影响的 unit 标识。</param>
public sealed record ShelfChangedEventArgs(ShelfChangeKind Kind, IReadOnlyList<UnitId> AffectedIds);
