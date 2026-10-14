using DeskTools.Domain.Units;

namespace DeskTools.Application.Abstractions;

/// <summary>置物架持久化仓储。实现负责文件格式、原子写与损坏容错。</summary>
public interface IShelfStore
{
    /// <summary>
    /// 读取全部 unit。文件不存在时返回空集合；文件损坏时由实现负责备份并返回空集合。
    /// </summary>
    Task<IReadOnlyList<ShelfUnit>> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>全量保存。</summary>
    Task SaveAsync(IReadOnlyList<ShelfUnit> units, CancellationToken cancellationToken = default);
}
