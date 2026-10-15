namespace DeskShelf.Domain.ValueObjects;

/// <summary>unit 的稳定标识。不依赖 UI，可在任意线程构造。</summary>
public readonly record struct UnitId(Guid Value)
{
    /// <summary>生成一个新的标识。</summary>
    public static UnitId New() => new(Guid.NewGuid());

    /// <summary>解析持久化字符串；非法输入返回 <c>null</c> 而不抛异常（用于容错加载）。</summary>
    public static UnitId? TryParse(string? raw)
        => Guid.TryParse(raw, out var guid) ? new UnitId(guid) : null;

    /// <inheritdoc />
    public override string ToString() => Value.ToString("D");
}
