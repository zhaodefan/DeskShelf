using DeskTools.Domain.Units;

namespace DeskTools.Application.Catalog;

/// <summary>
/// 一种 unit 类型在 UI 上的呈现规格。
/// 表现层据此渲染，<b>不得</b>出现 <c>if (unit is TextUnit)</c> 之类的类型分支 ——
/// 新增 unit 类型只需在 <see cref="UnitCatalog"/> 注册一条记录。
/// </summary>
/// <param name="Kind">unit 类型。</param>
/// <param name="DisplayName">类型显示名（右键菜单、无障碍朗读使用）。</param>
/// <param name="IconKey">图标键，映射到表现层的矢量图标资源（例如 <c>Icon.Text</c>）。</param>
/// <param name="ViewKey">视图键，映射到表现层的 DataTemplate 资源（例如 <c>View.Unit.Text</c>）。</param>
/// <param name="Capabilities">能力声明，决定按钮与菜单项的可用性。</param>
/// <param name="SupportsPaste">能否由剪贴板粘贴产生（文件夹 unit 为 <c>false</c>）。</param>
/// <param name="SupportsDrop">能否由拖拽产生。</param>
/// <param name="DefaultMaxLines">文本类 unit 默认展示行数，非文本类型忽略。</param>
public sealed record UnitPresentation(
    UnitKind Kind,
    string DisplayName,
    string IconKey,
    string ViewKey,
    UnitCapabilities Capabilities,
    bool SupportsPaste,
    bool SupportsDrop,
    int DefaultMaxLines = 0);

/// <summary>
/// unit 类型的注册表（<b>封闭枚举 + 注册表</b>已确认方案）。
/// 这里是「类型 → 呈现规格」的唯一事实来源，并由单元测试保证枚举值全部注册。
/// </summary>
public static class UnitCatalog
{
    private static readonly Dictionary<UnitKind, UnitPresentation> Registry = new()
    {
        [UnitKind.Text] = new UnitPresentation(
            Kind: UnitKind.Text,
            DisplayName: "文本",
            IconKey: "Icon.Text",
            ViewKey: "View.Unit.Text",
            Capabilities: new UnitCapabilities(CanCopyToClipboard: true, CanEditText: true, OpenBehavior: UnitOpenBehavior.None),
            SupportsPaste: true,
            SupportsDrop: true,
            DefaultMaxLines: 8),

        [UnitKind.Image] = new UnitPresentation(
            Kind: UnitKind.Image,
            DisplayName: "图片",
            IconKey: "Icon.Image",
            ViewKey: "View.Unit.Image",
            Capabilities: new UnitCapabilities(CanCopyToClipboard: true, CanEditText: false, OpenBehavior: UnitOpenBehavior.OpenImage),
            SupportsPaste: true,
            SupportsDrop: true,
            DefaultMaxLines: 0),

        [UnitKind.Folder] = new UnitPresentation(
            Kind: UnitKind.Folder,
            DisplayName: "文件夹",
            IconKey: "Icon.Folder",
            ViewKey: "View.Unit.Folder",
            Capabilities: new UnitCapabilities(CanCopyToClipboard: false, CanEditText: false, OpenBehavior: UnitOpenBehavior.OpenFolder),
            SupportsPaste: false,
            SupportsDrop: true,
            DefaultMaxLines: 2),
    };

    /// <summary>全部已注册的 unit 类型。</summary>
    public static IReadOnlyCollection<UnitKind> RegisteredKinds => Registry.Keys;

    /// <summary>取指定类型的呈现规格；未注册时抛出（由启动自检与单测兜住，不应在运行期发生）。</summary>
    public static UnitPresentation Get(UnitKind kind)
        => Registry.TryGetValue(kind, out var presentation)
            ? presentation
            : throw new KeyNotFoundException($"unit 类型 {kind} 未在 UnitCatalog 中注册。请在 Application/Catalog/UnitCatalog.cs 中补登记。");

    /// <summary>按 unit 实例取呈现规格。</summary>
    public static UnitPresentation For(ShelfUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return Get(unit.Kind);
    }

    /// <summary>启动自检：确认枚举中的每个值都已注册，返回缺失项。</summary>
    public static IReadOnlyList<UnitKind> FindUnregisteredKinds()
        => Enum.GetValues<UnitKind>().Where(kind => !Registry.ContainsKey(kind)).ToList();
}
