using System.Net;
using System.Text.RegularExpressions;

namespace DeskShelf.Application.Services;

/// <summary>
/// 剪贴板 / 拖拽数据中 <c>CF_HTML</c> 格式 → 纯文本的降级转换（平台无关的纯文本处理）。
/// </summary>
/// <remarks>
/// 存在的意义：从浏览器复制或拖拽文字时，很多来源<b>只</b>提供 HTML Format 而不提供
/// UnicodeText，不做这层降级就会出现「明明拖了文字却存不进去」。
/// </remarks>
public static partial class HtmlTextConverter
{
    /// <summary>把 CF_HTML 负载（含头部）或裸 HTML 片段转换为纯文本。</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var fragment = ExtractFragment(html);
        return StripTags(fragment);
    }

    /// <summary>解析 CF_HTML 头部，取出 <c>StartFragment</c>..<c>EndFragment</c> 之间的正文。</summary>
    internal static string ExtractFragment(string html)
    {
        var startMarker = html.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var endMarker = html.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);

        if (startMarker >= 0 && endMarker > startMarker)
        {
            var start = startMarker + "<!--StartFragment-->".Length;
            return html[start..endMarker];
        }

        // 回退：使用头部的 StartHTML/EndHTML 偏移量
        var startHtml = ReadOffset(html, "StartHTML");
        var endHtml = ReadOffset(html, "EndHTML");
        if (startHtml is > 0 && endHtml is > 0 && endHtml > startHtml && endHtml <= html.Length)
        {
            return html[startHtml.Value..endHtml.Value];
        }

        return html;
    }

    private static int? ReadOffset(string html, string name)
    {
        var match = Regex.Match(html, name + @"\s*:\s*(\d+)", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }

    /// <summary>剥离标签与实体，并把块级标签转换为换行。</summary>
    internal static string StripTags(string html)
    {
        if (html.Length == 0)
        {
            return string.Empty;
        }

        var text = html;

        // 丢弃 script / style 内容
        text = ScriptStyleRegex().Replace(text, string.Empty);

        // 块级结束标签与 <br> 视为换行
        text = BlockBreakRegex().Replace(text, "\n");

        // 列表项前加项目符号，保留结构感
        text = ListItemRegex().Replace(text, "\n• ");

        // 移除剩余标签
        text = AnyTagRegex().Replace(text, string.Empty);

        // 解码 HTML 实体
        text = WebUtility.HtmlDecode(text);

        // 压缩空白：保留换行，行内多余空格合并
        text = HorizontalSpaceRegex().Replace(text, " ");
        text = BlankLineRegex().Replace(text, "\n\n");

        return text.Trim();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex HorizontalSpaceRegex();

    [GeneratedRegex(@"\n\s*\n\s*(\n\s*)+")]
    private static partial Regex BlankLineRegex();
}
