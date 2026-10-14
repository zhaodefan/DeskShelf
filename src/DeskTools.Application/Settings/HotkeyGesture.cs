using System.Text;

namespace DeskTools.Application.Settings;

/// <summary>
/// 全局热键手势。默认 <c>Alt+X</c>（已确认）。
/// 注意：<c>Alt+字母</c> 属于菜单助记符空间，可能与前台程序的菜单加速键冲突，
/// 因此注册失败必须降级为「提示 + 允许在设置中改键」。
/// </summary>
public sealed record HotkeyGesture
{
    /// <summary>无修饰键的裸键（例如 F8）是合法的；<c>Alt+F4</c> 之类的系统保留组合会被系统优先处理。</summary>
    public bool Ctrl { get; init; }

    /// <summary>Alt 修饰键。</summary>
    public bool Alt { get; init; } = true;

    /// <summary>Shift 修饰键。</summary>
    public bool Shift { get; init; }

    /// <summary>Win 修饰键。</summary>
    public bool Win { get; init; }

    /// <summary>主键名（<c>System.Windows.Forms.Keys</c> 风格名称，例如 <c>X</c> / <c>F8</c>）。</summary>
    public string Key { get; init; } = "X";

    /// <summary>默认手势 Alt+X。</summary>
    public static HotkeyGesture Default { get; } = new();

    /// <summary>是否为有效手势（至少含一个修饰键与一个主键）。</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Key) && (Ctrl || Alt || Shift || Win) && !string.Equals(Key, "None", StringComparison.OrdinalIgnoreCase);

    /// <summary>显示文本，例如 <c>Alt+X</c>。</summary>
    public string DisplayText
    {
        get
        {
            var sb = new StringBuilder();
            if (Ctrl)
            {
                sb.Append("Ctrl+");
            }

            if (Alt)
            {
                sb.Append("Alt+");
            }

            if (Shift)
            {
                sb.Append("Shift+");
            }

            if (Win)
            {
                sb.Append("Win+");
            }

            return sb.Append(Key).ToString();
        }
    }

    /// <inheritdoc />
    public override string ToString() => DisplayText;
}
