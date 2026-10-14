namespace DeskTools.Domain.ValueObjects;

/// <summary>
/// 图片在本地存储中的键（相对于图片目录的文件名，例如 <c>ab12cd34ef56.png</c>）。
/// 领域层只持有键，<b>不</b>接触文件系统，落盘与读取由基础设施层实现。
/// </summary>
public readonly record struct StorageKey(string Value)
{
    /// <summary>是否为空键。</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}
