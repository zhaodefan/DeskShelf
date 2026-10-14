using System.Collections.ObjectModel;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;

namespace DeskTools.Domain.Shelf;

/// <summary>
/// 置物架聚合根：持有<b>有序</b> unit 集合，是所有增删改移的唯一入口。
/// 内部使用 <see cref="ObservableCollection{T}"/>（BCL 类型，非 UI 类型），
/// 使表现层可直接绑定并获得增量通知，避免全量刷新导致的滚动跳动。
/// </summary>
public sealed class Shelf
{
    private readonly ObservableCollection<ShelfUnit> _units = new();
    private readonly ReadOnlyObservableCollection<ShelfUnit> _readOnlyUnits;

    /// <summary>创建空置物架。</summary>
    public Shelf()
    {
        _readOnlyUnits = new ReadOnlyObservableCollection<ShelfUnit>(_units);
    }

    /// <summary>只读的有序 unit 集合（可绑定）。</summary>
    public ReadOnlyObservableCollection<ShelfUnit> Units => _readOnlyUnits;

    /// <summary>当前 unit 数量。</summary>
    public int Count => _units.Count;

    /// <summary>
    /// 追加到末尾（已确认策略：新增 unit 追加到列表末尾，保持「先到的先看到」）。
    /// </summary>
    public void Append(ShelfUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _units.Add(unit);
    }

    /// <summary>批量追加，返回实际加入的 unit。</summary>
    public IReadOnlyList<ShelfUnit> AppendRange(IEnumerable<ShelfUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var added = new List<ShelfUnit>();
        foreach (var unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit);
            _units.Add(unit);
            added.Add(unit);
        }

        return added;
    }

    /// <summary>插入到指定下标（拖拽落点定位用）。下标会被收敛到合法范围。</summary>
    public void Insert(int index, ShelfUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        _units.Insert(Clamp(index), unit);
    }

    /// <summary>按标识移除，返回被移除的 unit（不存在则返回 <c>null</c>）。</summary>
    public ShelfUnit? Remove(UnitId id)
    {
        var index = IndexOf(id);
        if (index < 0)
        {
            return null;
        }

        var unit = _units[index];
        _units.RemoveAt(index);
        return unit;
    }

    /// <summary>按标识批量移除，返回「原下标 + unit」的有序快照，供撤销精确还原。</summary>
    public IReadOnlyList<(int Index, ShelfUnit Unit)> RemoveMany(IEnumerable<UnitId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        // 先按原下标升序解析目标，再逆序移除，避免「边遍历边移除」导致的下标漂移
        var targets = ids
            .Select(id => (Id: id, Index: IndexOf(id)))
            .Where(t => t.Index >= 0)
            .OrderBy(t => t.Index)
            .ToList();

        var removed = new List<(int Index, ShelfUnit Unit)>(targets.Count);
        foreach (var (_, index) in targets)
        {
            removed.Add((index, _units[index]));
        }

        for (var i = targets.Count - 1; i >= 0; i--)
        {
            _units.RemoveAt(targets[i].Index);
        }

        return removed;
    }

    /// <summary>按「原下标 + unit」快照还原（逆序插入以保证下标正确）。</summary>
    public void Restore(IReadOnlyList<(int Index, ShelfUnit Unit)> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var (index, unit) in snapshot.OrderBy(t => t.Index))
        {
            _units.Insert(Clamp(index), unit);
        }
    }

    /// <summary>将指定 unit 移动到新下标（拖拽排序用）。</summary>
    public bool Move(UnitId id, int newIndex)
    {
        var oldIndex = IndexOf(id);
        if (oldIndex < 0)
        {
            return false;
        }

        var target = Clamp(newIndex);
        if (target == oldIndex)
        {
            return false;
        }

        _units.Move(oldIndex, target);
        return true;
    }

    /// <summary>清空全部 unit，返回被清空的内容快照（供撤销）。</summary>
    public IReadOnlyList<ShelfUnit> ClearAll()
    {
        var snapshot = _units.ToList();
        _units.Clear();
        return snapshot;
    }

    /// <summary>查找下标，不存在返回 -1。</summary>
    public int IndexOf(UnitId id)
    {
        for (var i = 0; i < _units.Count; i++)
        {
            if (_units[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>按标识查找 unit。</summary>
    public ShelfUnit? Find(UnitId id)
    {
        var index = IndexOf(id);
        return index < 0 ? null : _units[index];
    }

    /// <summary>是否已存在指纹相同的内容（去重用，当前未默认启用）。</summary>
    public bool ContainsFingerprint(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return false;
        }

        foreach (var unit in _units)
        {
            if (string.Equals(unit.ContentFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>用持久化数据整体替换内容（加载时使用，不触发逐项通知风暴）。</summary>
    public void ReplaceAll(IEnumerable<ShelfUnit> units)
    {
        ArgumentNullException.ThrowIfNull(units);
        _units.Clear();
        foreach (var unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit);
            _units.Add(unit);
        }
    }

    private int Clamp(int index) => index < 0 ? 0 : index > _units.Count ? _units.Count : index;
}
