using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 剪贴板格式取证：只枚举格式编号与名称，<b>不读取数据</b>。
/// 早前版本对每个格式调 <c>GlobalSize</c>，遇到 CF_BITMAP 这类非 HGLOBAL 句柄会
/// 触发 STATUS_HEAP_CORRUPTION（0xC0000374）直接崩掉宿主进程，故移除该调用。
/// </summary>
public static class ClipProbe
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetClipboardFormatName(uint format, StringBuilder lpszFormatName, int cchMaxCount);

    /// <summary>格式编号 → 名称。</summary>
    public static string FormatName(uint format)
    {
        var sb = new StringBuilder(256);
        var n = GetClipboardFormatName(format, sb, sb.Capacity);
        if (n > 0)
        {
            return sb.ToString(0, n);
        }

        switch (format)
        {
            case 1: return "CF_TEXT";
            case 2: return "CF_BITMAP";
            case 3: return "CF_METAFILEPICT";
            case 8: return "CF_DIB";
            case 13: return "CF_UNICODETEXT";
            case 17: return "CF_DIBV5";
            default: return "标准#" + format;
        }
    }

    /// <summary>枚举剪贴板全部格式，返回 "编号 TAB 名称" 列表。</summary>
    public static string[] Formats()
    {
        var list = new List<string>();
        if (!OpenClipboard(IntPtr.Zero))
        {
            return new[] { "!! OpenClipboard 失败（可能被其他进程占用）" };
        }

        try
        {
            uint f = 0;
            while ((f = EnumClipboardFormats(f)) != 0)
            {
                list.Add(f + "\t" + FormatName(f));
            }
        }
        finally
        {
            CloseClipboard();
        }

        return list.ToArray();
    }
}
