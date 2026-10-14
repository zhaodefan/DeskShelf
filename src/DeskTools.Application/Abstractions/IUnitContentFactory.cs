using DeskTools.Domain;
using DeskTools.Domain.Units;

namespace DeskTools.Application.Abstractions;

/// <summary>
/// 把归一化的外部内容转换为领域 unit。这是<b>唯一</b>的 unit 创建入口：
/// 剪贴板粘贴、拖拽落盘、将来的截图/浏览器插件都必须经过它，
/// 从而保证「新增 unit 类型」只需改动本接口的实现，不必触碰用例与 UI。
/// </summary>
public interface IUnitContentFactory
{
    /// <summary>
    /// 尝试把原始内容转换为若干个 unit。
    /// </summary>
    /// <param name="content">已归一化的外部内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>
    /// 转换结果：成功时为 1..n 个 unit；内容不受支持时由 <see cref="UnitCreationResult.Warnings"/> 给出原因。
    /// </returns>
    Task<UnitCreationResult> CreateAsync(RawContent content, CancellationToken cancellationToken = default);
}

/// <summary>unit 创建结果。</summary>
/// <param name="Units">成功创建的 unit 列表（按拖入顺序）。</param>
/// <param name="Warnings">被跳过的内容说明（每项一条人类可读文案），全部成功时为空集合。</param>
public sealed record UnitCreationResult(IReadOnlyList<ShelfUnit> Units, IReadOnlyList<string> Warnings)
{
    /// <summary>是否至少创建了一个 unit。</summary>
    public bool HasAny => Units.Count > 0;

    /// <summary>是否存在被跳过的内容。</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>被跳过的内容说明（合并为一句），无警告时为 <c>null</c>。</summary>
    public string? WarningText => Warnings.Count == 0 ? null : string.Join("；", Warnings);

    /// <summary>全部失败的空结果。</summary>
    public static UnitCreationResult Unsupported(params string[] warnings)
        => new(Array.Empty<ShelfUnit>(), warnings.Length == 0 ? new[] { "该内容暂不支持存放。" } : warnings);

    /// <summary>全部成功。</summary>
    public static UnitCreationResult Success(IReadOnlyList<ShelfUnit> units) => new(units, Array.Empty<string>());

    /// <summary>部分成功（存在被跳过的内容）。</summary>
    public static UnitCreationResult Partial(IReadOnlyList<ShelfUnit> units, IReadOnlyList<string> warnings)
        => new(units, warnings);
}
