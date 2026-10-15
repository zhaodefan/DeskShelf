using System.Windows.Media.Imaging;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App.Services;

/// <summary>
/// 缩略图加载器：字节读取走 <see cref="ImageBytesCache"/>（有界 LRU），
/// 解码在后台线程完成并 <c>Freeze()</c>，因此结果可以安全地绑定到 UI。
/// </summary>
/// <remarks>
/// 表现层服务：这里出现 WPF 类型是合理的 —— 解码结果本身就是 WPF 的
/// <see cref="BitmapImage"/>，它无法也不应该出现在领域层或用例层。
/// </remarks>
public sealed class ThumbnailLoader
{
    /// <summary>解码宽度上限（像素）。面板宽度有限，无需解码原图全尺寸。</summary>
    public const int DecodePixelWidth = 480;

    private readonly ImageBytesCache _cache;
    private readonly IImageBytesSource _source;
    private readonly ILogger<ThumbnailLoader> _logger;
    private readonly Dictionary<StorageKey, BitmapImage?> _decoded = new();
    private readonly object _gate = new();

    /// <summary>创建加载器。</summary>
    public ThumbnailLoader(ImageBytesCache cache, IImageBytesSource source, ILogger<ThumbnailLoader> logger)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>异步获取解码后的缩略图；失败返回 <c>null</c>（视图显示占位）。</summary>
    public async Task<BitmapImage?> GetAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        lock (_gate)
        {
            if (_decoded.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        byte[]? bytes;
        try
        {
            bytes = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        var decoded = bytes is null or { Length: 0 }
            ? null
            : await Task.Run(() => Decode(bytes), cancellationToken).ConfigureAwait(false);

        if (decoded is null)
        {
            _logger.LogWarning("图片解码失败：{Key}（{Bytes} 字节）", key.Value, bytes?.Length ?? 0);
        }
        else
        {
            _logger.LogDebug("缩略图已就绪：{Key} → {Width}×{Height}（{Bytes} 字节）",
                key.Value, decoded.PixelWidth, decoded.PixelHeight, bytes!.Length);
        }

        lock (_gate)
        {
            _decoded[key] = decoded;
        }

        return decoded;
    }

    /// <summary>清空已解码缓存。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _decoded.Clear();
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.DecodePixelWidth = DecodePixelWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
