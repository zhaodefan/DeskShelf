using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using DeskShelf.Application.Abstractions;
using DeskShelf.Domain;
using DeskShelf.Domain.Units;
using DeskShelf.Infrastructure.Clipboard;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Infrastructure.Clipboard;

/// <summary>
/// WPF 剪贴板适配。这是全应用<b>唯一</b>允许出现 <c>System.Windows.Clipboard</c> 的地方。
/// 内容的格式识别与降级链复用 <see cref="DataObjectReader"/>，与拖拽通道保持一致。
/// </summary>
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

    private RawContent? ReadCore()
    {
        var payload = DataObjectReader.TryExtractFromClipboard(MaxAttempts, RetryDelayMs);
        if (payload is null)
        {
            _logger.LogDebug("剪贴板为空或始终被占用。");
            return null;
        }

        var content = DataObjectReader.ToRawContent(payload, UnitOrigin.Clipboard);
        if (content is null)
        {
            _logger.LogDebug("剪贴板中没有可存放的内容。");
        }

        return content;
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
        if (content.Kind == RawContentKind.Image && content.ImageBytes is { Length: > 0 } bytes)
        {
            var data = new DataObject();

            // 同时提供 PNG（现代程序）与位图（老旧程序），提高兼容性
            using var pngStream = new MemoryStream(bytes, writable: false);
            data.SetData("PNG", pngStream, autoConvert: false);

            try
            {
                using var bitmapStream = new MemoryStream(bytes, writable: false);
                var frame = BitmapFrame.Create(bitmapStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
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
            var data = new DataObject();
            var collection = new StringCollection();
            collection.AddRange(content.Paths.ToArray());
            data.SetFileDropList(collection);
            data.SetText(string.Join(Environment.NewLine, content.Paths));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
            return;
        }

        System.Windows.Clipboard.SetText(content.Text ?? string.Empty);
    }

    private static bool IsTransient(Exception ex)
        => ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException;

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
}
