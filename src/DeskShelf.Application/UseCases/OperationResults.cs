using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Application.UseCases;

/// <summary>一次内容摄取（粘贴或拖拽）的执行结果。</summary>
/// <param name="Success">是否成功新增了 unit。</param>
/// <param name="AddedIds">新增 unit 的标识。</param>
/// <param name="Message">给用户看的提示（成功时的简述或失败原因）。</param>
public sealed record IngestResult(bool Success, IReadOnlyList<UnitId> AddedIds, string Message)
{
    /// <summary>无内容可摄取。</summary>
    public static IngestResult Nothing(string message) => new(false, Array.Empty<UnitId>(), message);

    /// <summary>成功新增。</summary>
    public static IngestResult Added(IReadOnlyList<UnitId> ids, string message) => new(true, ids, message);
}

/// <summary>删除操作的结果。</summary>
/// <param name="RemovedCount">实际删除数量。</param>
/// <param name="CanUndo">是否可撤销（对应撤销栈是否已记录）。</param>
/// <param name="Message">用户提示。</param>
public sealed record MutationResult(int RemovedCount, bool CanUndo, string Message)
{
    /// <summary>无变化。</summary>
    public static MutationResult NoChange(string message) => new(0, false, message);
}

/// <summary>复制到剪贴板的结果。</summary>
/// <param name="Success">是否成功写入剪贴板。</param>
/// <param name="Message">用户提示（混合类型批量复制等降级行为会在此说明）。</param>
public sealed record CopyResult(bool Success, string Message)
{
    /// <summary>失败。</summary>
    public static CopyResult Failed(string message) => new(false, message);

    /// <summary>成功。</summary>
    public static CopyResult Ok(string message) => new(true, message);
}
