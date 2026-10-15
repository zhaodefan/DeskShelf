namespace DeskShelf.Application.Undo;

/// <summary>
/// 可撤销操作。实现自行持有执行所需的快照，保证「撤销」能精确还原现场
/// （例如删除多选后按下标逆序插回）。
/// </summary>
public interface IUndoableAction
{
    /// <summary>用户可见的操作描述，例如「已移除 3 项」。</summary>
    string Description { get; }

    /// <summary>执行（首次调用）或重做。</summary>
    void Apply();

    /// <summary>撤销。</summary>
    void Revert();
}

/// <summary>
/// 内存撤销栈（不持久化，已确认）。默认保留最近 10 批操作；
/// 未实现重做栈 —— 面板是暂存区，撤销后的重做价值低且会与编辑器自身的 Ctrl+Z 混淆。
/// </summary>
public sealed class UndoStack
{
    /// <summary>默认保留的操作批次上限。</summary>
    public const int DefaultCapacity = 10;

    private readonly LinkedList<IUndoableAction> _actions = new();

    /// <summary>创建撤销栈。</summary>
    /// <param name="capacity">保留批次上限。</param>
    public UndoStack(int capacity = DefaultCapacity)
    {
        Capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>保留批次上限。</summary>
    public int Capacity { get; }

    /// <summary>当前可撤销批次数量。</summary>
    public int Count => _actions.Count;

    /// <summary>是否可撤销。</summary>
    public bool CanUndo => _actions.Count > 0;

    /// <summary>最近一次可撤销操作的描述。</summary>
    public string? PeekDescription => _actions.Last?.Value.Description;

    /// <summary>记录一个<b>已经执行完毕</b>的操作（不再重复 Apply）。</summary>
    public void Record(IUndoableAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _actions.AddLast(action);
        while (_actions.Count > Capacity)
        {
            _actions.RemoveFirst();
        }
    }

    /// <summary>撤销最近一次操作；无可撤销内容时返回 <c>null</c>。</summary>
    public IUndoableAction? Undo()
    {
        var node = _actions.Last;
        if (node is null)
        {
            return null;
        }

        _actions.RemoveLast();
        node.Value.Revert();
        return node.Value;
    }

    /// <summary>清空撤销栈（例如加载新数据、退出前）。</summary>
    public void Clear() => _actions.Clear();
}
