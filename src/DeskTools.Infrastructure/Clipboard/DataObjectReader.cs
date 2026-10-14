using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskTools.Application.Services;

namespace DeskTools.Infrastructure.Clipboard;

/// <summary>
/// WPF <see cref="IDataObject"/> 的原始内容读取。
/// 剪贴板读取与拖拽落盘<b>共用</b>本类，保证两条输入通道的格式优先级与降级链完全一致
/// （否则会出现「复制能存、拖动存不进去」这类难查的差异）。
/// </summary>
public static class DataObjectReader
{
    /// <summary>HTML 剪贴板格式名（CF_HTML）。</summary>
    public const string HtmlFormat = "HTML Format";

    /// <summary>
    /// 从系统剪贴板提取原始内容（含占用重试）。
    /// 必须在 STA 线程（UI 线程）调用。
    /// </summary>
    /// <param name="maxAttempts">最大尝试次数。</param>
    /// <param name="retryDelayMs">两次尝试之间的间隔。</param>
    /// <returns>原始内容；剪贴板为空或始终被占用时返回 <c>null</c>。</returns>
    public static DataObjectPayload? TryExtractFromClipboard(int maxAttempts = 5, int retryDelayMs = 40)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return TryReadPayload(System.Windows.Clipboard.GetDataObject());
            }
            catch (Exception) when (attempt < maxAttempts)
            {
                // 剪贴板被其他进程短暂占用：等待后重试
                Thread.Sleep(retryDelayMs);
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>读取文件/文件夹路径列表。</summary>
    public static IReadOnlyList<string>? TryReadFilePaths(IDataObject? data)
    {
        if (data is null || !GetDataPresent(data, DataFormats.FileDrop))
        {
            return null;
        }

        var raw = GetData(data, DataFormats.FileDrop);
        var files = raw switch
        {
            string[] array => array,
            StringCollection collection => collection.Cast<string>().ToArray(),
            _ => null,
        };

        return files is null or { Length: 0 } ? null : files;
    }

    /// <summary>读取位图并编码为 PNG 字节。</summary>
    public static byte[]? TryReadImagePng(IDataObject? data)
    {
        if (data is null || !GetDataPresent(data, DataFormats.Bitmap))
        {
            return null;
        }

        if (GetData(data, DataFormats.Bitmap) is not BitmapSource source)
        {
            return null;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>读取纯文本（依次尝试 UnicodeText / Text / StringFormat）。</summary>
    public static string? TryReadText(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }

        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat })
        {
            if (!GetDataPresent(data, format))
            {
                continue;
            }

            if (GetData(data, format) is string text && text.Trim().Length > 0)
            {
                return text;
            }
        }

        return null;
    }

    /// <summary>
    /// 读取 CF_HTML 负载（含头部）。返回<b>未处理</b>的原始字符串，
    /// 由调用方决定是否做 HTML→纯文本降级。
    /// </summary>
    public static string? TryReadHtmlPayload(IDataObject? data)
    {
        if (data is null || !GetDataPresent(data, HtmlFormat))
        {
            return null;
        }

        var raw = GetData(data, HtmlFormat);
        return raw switch
        {
            string text => text,
            MemoryStream stream => DecodeHtmlBytes(stream.ToArray()),
            byte[] bytes => DecodeHtmlBytes(bytes),
            _ => null,
        };
    }

    /// <summary>依次尝试各格式，返回第一个可用的原始内容。</summary>
    public static DataObjectPayload? TryReadPayload(IDataObject? data)
    {
        if (data is null)
        {
            return null;
        }

        if (TryReadFilePaths(data) is { Count: > 0 } paths)
        {
            return new DataObjectPayload { FilePaths = paths };
        }

        if (TryReadImagePng(data) is { Length: > 0 } imageBytes)
        {
            return new DataObjectPayload { ImageBytes = imageBytes };
        }

        if (TryReadText(data) is { Length: > 0 } text)
        {
            return new DataObjectPayload { Text = text };
        }

        if (TryReadHtmlPayload(data) is { Length: > 0 } html)
        {
            return new DataObjectPayload { Html = html };
        }

        return null;
    }

    /// <summary>
    /// 把原始内容归一化为领域可用的 <c>RawContent</c>：
    /// 处理图片优先、HTML→纯文本降级等跨通道统一策略。
    /// </summary>
    public static Domain.RawContent? ToRawContent(DataObjectPayload? payload, Domain.Units.UnitOrigin origin)
    {
        if (payload is null || !payload.HasContent)
        {
            return null;
        }

        if (payload.FilePaths is { Count: > 0 } paths)
        {
            return Domain.RawContent.FromPaths(paths, origin);
        }

        if (payload.ImageBytes is { Length: > 0 } imageBytes)
        {
            return Domain.RawContent.FromImage(imageBytes, "png", origin);
        }

        if (payload.Text is { Length: > 0 } text)
        {
            return Domain.RawContent.FromText(text, origin);
        }

        if (payload.Html is { Length: > 0 } html)
        {
            var plain = HtmlTextConverter.ToPlainText(html);
            return plain.Length > 0 ? Domain.RawContent.FromText(plain, origin) : null;
        }

        return null;
    }

    private static bool GetDataPresent(IDataObject data, string format)
    {
        try
        {
            return data.GetDataPresent(format);
        }
        catch (Exception)
        {
            // 管理员权限的源进程会触发 UIPI 拒绝
            return false;
        }
    }

    private static object? GetData(IDataObject data, string format)
    {
        try
        {
            return data.GetData(format);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string DecodeHtmlBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        // CF_HTML 头部声明了编码；浏览器普遍使用 UTF-8
        var asUtf8 = Encoding.UTF8.GetString(bytes);
        return asUtf8.Contains("Encoding:", StringComparison.OrdinalIgnoreCase) ||
               asUtf8.Contains("Version:", StringComparison.OrdinalIgnoreCase)
            ? asUtf8
            : Encoding.Default.GetString(bytes);
    }
}

/// <summary>从 <see cref="IDataObject"/> 中读出的原始内容（各字段互斥，最多一个非空）。</summary>
public sealed record DataObjectPayload
{
    /// <summary>文件/文件夹路径。</summary>
    public IReadOnlyList<string>? FilePaths { get; init; }

    /// <summary>PNG 编码的图片字节。</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>纯文本。</summary>
    public string? Text { get; init; }

    /// <summary>CF_HTML 原始负载（含头部）。</summary>
    public string? Html { get; init; }

    /// <summary>是否存在任何可用内容。</summary>
    public bool HasContent => FilePaths is { Count: > 0 } || ImageBytes is { Length: > 0 } ||
                              !string.IsNullOrEmpty(Text) || !string.IsNullOrEmpty(Html);
}
