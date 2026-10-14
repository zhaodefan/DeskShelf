using DeskTools.Domain.ValueObjects;

namespace DeskTools.Domain.Units;

/// <summary>
/// 面板上的一个「卡片」的基类。所有类型都必须声明 <see cref="Capabilities"/>，
/// UI 与用例层据能力而非具体类型做分支，这是 unit 类型可扩展的关键。
/// </summary>
public abstract class ShelfUnit
{
    /// <summary>受保护构造，仅供派生类型与工厂使用。</summary>
    /// <param name="id">稳定标识。</param>
    /// <param name="origin">来源。</param>
    /// <param name="createdAt">创建时间（UTC）。</param>
    protected ShelfUnit(UnitId id, UnitOrigin origin, DateTimeOffset createdAt)
    {
        Id = id;
        Origin = origin;
        CreatedAt = createdAt;
    }

    /// <summary>稳定标识。</summary>
    public UnitId Id { get; }

    /// <summary>类型标识。</summary>
    public abstract UnitKind Kind { get; }

    /// <summary>能力声明。</summary>
    public abstract UnitCapabilities Capabilities { get; }

    /// <summary>来源。</summary>
    public UnitOrigin Origin { get; internal set; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; internal set; }

    /// <summary>
    /// 用于去重与检索的规范化内容指纹。
    /// 例如文本为正文本身，图片为存储键，文件夹为规范化后的路径。
    /// </summary>
    public abstract string ContentFingerprint { get; }

    /// <summary>
    /// 可读的内容摘要，用于日志、无障碍朗读与「复制路径/复制文本」等通用动作。
    /// </summary>
    public abstract string ContentSummary { get; }
}
