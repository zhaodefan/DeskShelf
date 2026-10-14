using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskTools.Application.Abstractions;
using DeskTools.Domain;
using DeskTools.Domain.Units;
using DeskTools.Infrastructure.Clipboard;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Clipboard;

/// <summary>
/// WPF 剪贴板适配。这是全应用<b>唯一</b>允许出现 <c>System.Windows.Clipboard</c> 的地方。
/// </summary>
/// <remarks>
/// 现实约束（决定了本类的复杂度）：
/// <list type="bullet">
/// <item>剪贴板可能被其他进程短暂占用，<c>Clipboard</c> 会抛 <c>COMException</c> —— 必须重试；</item>
/// <item>浏览器拖拽/复制文本时常常只提供 <c>HTML Format</c> 而不提供 <c>UnicodeText</c> —— 必须做 HTML→纯文本降级；</item>
/// <item>剪贴板内容可能以「字节流 / MemoryStream / string」三种形态出现 —— 必须逐一尝试。</item>
/// </list>
/// </remarks>
public sealed class WpfClipboardService : IClipboardService
{
    private const int MaxAttempts = 5;
    private const int RetryDelayMs = 40;

    private readonly ILogger<WpfClipboardService> _logger;

    /// <summary>创建剪贴板服务。</summary>
    public WpfClipboardService(ILogger<WpfClipboardService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<RawContent?> TryReadAsync(CancellationToken cancellationToken = default)
        => RunOnUiThreadAsync(ReadCore, cancellationToken);

    /// <inheritdoc />
    public Task WriteAsync(RawContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return RunOnUiThreadAsync(() => WriteCore(content), cancellationToken);
    }

    private static Task<T> RunOnUiThreadAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal).Task;
    }

    private static Task RunOnUiThreadAsync(Action action, CancellationToken cancellationToken)
        => RunOnUiThreadAsync<object?>(
            () =>
            {
                action();
                return null;
            },
            cancellationToken);

    private RawContent? ReadCore()
    {
        IDataObject? data = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                data = System.Windows.Clipboard.GetDataObject();
                break;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogDebug("读取剪贴板被占用，第 {Attempt} 次重试。", attempt);
                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取剪贴板失败。");
                return null;
            }
        }

        if (data is null)
        {
            return null;
        }

        // 优先级：文件列表 > 位图 > 纯文本 > HTML 文本
        if (TryReadFileDrop(data) is { } paths)
        {
            return RawContent.FromPaths(paths, UnitOrigin.Clipboard);
        }

        if (TryReadImage(data) is { } imageBytes)
        {
            return RawContent.FromImage(imageBytes, "png", UnitOrigin.Clipboard);
        }

        if (TryReadText(data) is { Length: > 0 } text)
        {
            return RawContent.FromText(text, UnitOrigin.Clipboard);
        }

        if (TryReadHtmlAsText(data) is { Length: > 0 } htmlText)
        {
            return RawContent.FromText(htmlText, UnitOrigin.Clipboard);
        }

        _logger.LogDebug("剪贴板中没有可存放的内容。");
        return null;
    }

    private void WriteCore(RawContent content)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                WriteOnce(content);
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxAttempts)
            {
                _logger.LogDebug("写入剪贴板被占用，第 {Attempt} 次重试。", attempt);
                Thread.Sleep(RetryDelayMs);
            }
        }

        // 最后一次失败必须抛出，让用例层把失败原因反馈给用户
        WriteOnce(content);
    }

    private static void WriteOnce(RawContent content)
    {
        var data = new DataObject();

        if (content.Kind == RawContentKind.Image && content.ImageBytes is { Length: > 0 } bytes)
        {
            // 写成 PNG（无损、体积小），同时附带位图以便老旧程序识别
            using var stream = new MemoryStream(bytes, writable: false);
            data.SetData("PNG", stream, autoConvert: false);

            try
            {
                var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                data.SetImage(frame);
            }
            catch (Exception)
            {
                // 部分来源的字节并非标准图片容器，PNG 通道已足够
            }

            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return;
        }

        if (content.Kind == RawContentKind.Paths && content.Paths.Count > 0)
        {
            var collection = new StringCollection();
            collection.AddRange(content.Paths.ToArray());
            data.SetFileDropList(collection);

            var joined = string.Join(Environment.NewLine, content.Paths);
            data.SetText(joined);
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return;
        }

        System.Windows.Clipboard.SetText(content.Text ?? string.Empty);
    }

    private static bool IsTransient(Exception ex)
        => ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException;

    private static IReadOnlyList<string>? TryReadFileDrop(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop))
            {
                return null;
            }

            if (data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
            {
                return null;
            }

            return files;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[]? TryReadImage(IDataObject data)
    {
        try
        {
            if (!data.GetDataPresent(DataFormats.Bitmap))
            {
                return null;
            }

            if (data.GetData(DataFormats.Bitmap) is not BitmapSource source)
            {
                return null;
            }

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadText(IDataObject data)
    {
        try
        {
            foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text, DataFormats.StringFormat })
            {
                if (!data.GetDataPresent(format))
                {
                    continue;
                }

                if (data.GetData(format) is string text && text.Trim().Length > 0)
                {
                    return text;
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryReadHtmlAsText(IDataObject data)
    {
        try
        {
            const string HtmlFormat = "HTML Format";
            if (!data.GetDataPresent(HtmlFormat))
            {
                return null;
            }

            var raw = ReadHtmlPayload(data);
            return string.IsNullOrWhiteSpace(raw) ? null : HtmlTextConverter.ToPlainText(raw);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ReadHtmlPayload(IDataObject data)
    {
        var raw = data.GetData("HTML Format");

        // CF_HTML 可能以 string / MemoryStream / byte[] 三种形态出现，全部兼容
        switch (raw)
        {
            case string text:
                return text;
            case MemoryStream stream:
                return DecodeHtmlBytes(stream.ToArray());
            case byte[] bytes:
                return DecodeHtmlBytes(bytes);
            default:
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
        var asUtf8 = System.Text.Encoding.UTF8.GetString(bytes);
        return asUtf8.Contains("Encoding:", StringComparison.OrdinalIgnoreCase) ||
               asUtf8.Contains("Version:", StringComparison.OrdinalIgnoreCase)
            ? asUtf8
            : System.Text.Encoding.Default.GetString(bytes);
    }
}
