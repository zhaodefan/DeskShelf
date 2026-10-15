namespace DeskShelf.Domain.Units;

/// <summary>
/// unit 类型标识。
/// 设计取舍（已与需求方确认）：使用<b>封闭枚举 + 注册表</b>而非开放字符串，
/// 以获得编译期穷尽性检查。将来若需真正开放类型，只需把
/// <c>UnitCatalog</c> 的实现换成反射/插件发现，已实现类型无需改动。
/// </summary>
public enum UnitKind
{
    /// <summary>文本 unit：可编辑、可复制文本。</summary>
    Text = 0,

    /// <summary>图片 unit：可复制图片、可调用系统看图程序打开。</summary>
    Image = 1,

    /// <summary>文件夹 unit（路径 unit）：仅可由资源管理器拖入，不可复制到剪贴板。</summary>
    Folder = 2,
}
