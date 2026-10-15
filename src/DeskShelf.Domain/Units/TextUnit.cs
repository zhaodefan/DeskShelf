using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Domain.Units;

/// <summary>
/// 文本 unit：面板内展示文本内容，双击可行内编辑，选中后 Ctrl+C 复制文本。
/// </summary>
public sealed class TextUnit : ShelfUnit
{
    /// <summary>单个文本 unit 允许的最大字符数，超出部分会被截断（防止误粘贴超大文本卡死 UI）。</summary>
    public const int MaxLength = 100_000;

    /// <summary>新建文本 unit（反序列化时同样使用此构造，以便保留原始创建时间）。</summary>
    public TextUnit(string text, UnitId id, UnitOrigin origin, DateTimeOffset createdAt)
        : base(id, origin, createdAt)
    {
        Text = Normalize(text);
    }

    /// <inheritdoc />
    public override UnitKind Kind => UnitKind.Text;

    /// <inheritdoc />
    public override UnitCapabilities Capabilities { get; } =
        new(CanCopyToClipboard: true, CanEditText: true, OpenBehavior: UnitOpenBehavior.None);

    /// <summary>文本正文。</summary>
    public string Text { get; private set; }

    /// <inheritdoc />
    public override string ContentFingerprint => Text;

    /// <inheritdoc />
    public override string ContentSummary => Text;

    /// <summary>更新文本内容（行内编辑提交时调用）。</summary>
    public void UpdateText(string text) => Text = Normalize(text);

    /// <summary>统一规范化：统一换行符、去掉首尾空白、按上限截断。</summary>
    public static string Normalize(string? text)
    {
        var value = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();

        return value.Length > MaxLength ? value[..MaxLength] : value;
    }
}
