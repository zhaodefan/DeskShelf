using System.Windows.Media.Imaging;
using DeskTools.Application.Abstractions;
using DeskTools.Domain.ValueObjects;
using DeskTools.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Imaging;

/// <summary>
/// 展示用图片字节读取：优先缩略图（小、快、省内存），缺失时回退原图。
/// 结果带一层有界内存缓存，避免滚动时反复读盘。
/// </summary>
public sealed class ImageBytesSource : IImageBytesSource
{
    private readonly AppPaths _paths;
    private readonly ILogger<ImageBytesSource> _logger;

    /// <summary>创建读取器。</summary>
    public ImageBytesSource(AppPaths paths, ILogger<ImageBytesSource> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            return null;
        }

        var fileName = System.IO.Path.GetFileName(key.Value);
        var originalPath = System.IO.Path.Combine(_paths.Images, fileName);
        if (fileName.EndsWith(".thumb.png", StringComparison.OrdinalIgnoreCase))
        {
            // 已经是缩略图键，直接使用
            return await ReadFileOrDefaultAsync(originalPath, cancellationToken).ConfigureAwait(false);
        }

        var thumbPath = System.IO.Path.Combine(
            _paths.Images,
            System.IO.Path.GetFileNameWithoutExtension(fileName) + ".thumb.png");

        var path = File.Exists(thumbPath) ? thumbPath : originalPath;
        return await ReadFileOrDefaultAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]?> ReadFileOrDefaultAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            _logger.LogWarning("展示用图片不存在：{Path}", path);
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "读取展示用图片失败：{Path}", path);
            return null;
        }
    }

    /// <summary>把字节解码为冻结的 <see cref="BitmapImage"/>（可跨线程使用），并限制解码尺寸。</summary>
    public static BitmapImage? Decode(byte[] bytes, int decodePixelWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = decodePixelWidth;
            }

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
