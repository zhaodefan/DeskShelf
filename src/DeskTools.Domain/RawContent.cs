using DeskTools.Domain.Units;

namespace DeskTools.Domain;

/// <summary>
/// 归一化后的「外部原始内容」。剪贴板粘贴与拖拽落盘<b>共用</b>这一模型，
/// 后续新增输入通道（例如截图工具、浏览器插件）只需产出 <see cref="RawContent"/>，
/// 无需改动 <c>ShelfService</c> 与任何 UI 代码。
/// </summary>
public sealed class RawContent
{
    private RawContent(RawContentKind kind, string? text, byte[]? imageBytes, string? imageFormat, IReadOnlyList<string> paths)
    {
        Kind = kind;
        Text = text;
        ImageBytes = imageBytes;
        ImageFormat = imageFormat;
        Paths = paths;
    }

    /// <summary>内容类别。</summary>
    public RawContentKind Kind { get; }

    /// <summary>文本内容（<see cref="RawContentKind.Text"/> 时非空）。</summary>
    public string? Text { get; }

    /// <summary>图片编码字节（<see cref="RawContentKind.Image"/> 时非空）。</summary>
    public byte[]? ImageBytes { get; }

    /// <summary>图片格式提示，例如 <c>png</c> / <c>jpg</c>；未知时为 <c>null</c>。</summary>
    public string? ImageFormat { get; }

    /// <summary>拖入的文件系统路径集合（<see cref="RawContentKind.Paths"/> 时非空）。</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>来源，用于写入 unit 元数据。</summary>
    public UnitOrigin Origin { get; init; } = UnitOrigin.Unknown;

    /// <summary>构造文本内容。</summary>
    public static RawContent FromText(string text, UnitOrigin origin = UnitOrigin.Unknown)
        => new(RawContentKind.Text, text, null, null, Array.Empty<string>()) { Origin = origin };

    /// <summary>构造图片内容。</summary>
    public static RawContent FromImage(byte[] bytes, string? format = null, UnitOrigin origin = UnitOrigin.Unknown)
        => new(RawContentKind.Image, null, bytes, format, Array.Empty<string>()) { Origin = origin };

    /// <summary>构造路径集合（拖拽文件/文件夹）。</summary>
    public static RawContent FromPaths(IReadOnlyList<string> paths, UnitOrigin origin = UnitOrigin.Unknown)
        => new(RawContentKind.Paths, null, null, null, paths) { Origin = origin };
}

/// <summary><see cref="RawContent"/> 的类别。</summary>
public enum RawContentKind
{
    /// <summary>纯文本。</summary>
    Text = 0,

    /// <summary>图片字节。</summary>
    Image = 1,

    /// <summary>文件系统路径集合。</summary>
    Paths = 2,
}
