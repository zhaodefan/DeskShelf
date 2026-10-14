using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using DeskTools.Application.Abstractions;
using DeskTools.Domain.ValueObjects;
using DeskTools.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Storage;

/// <summary>
/// 基于 WIC（WPF 图像 API）的图片本地存储。
/// 约定：文件名为内容哈希（前 16 位十六进制），因此<b>重复粘贴同一张图片不会重复占空间</b>；
/// 缩略图与原件分离，面板只解码缩略图，避免大图撑爆内存。
/// </summary>
public sealed class ImageFileStore : IImageStore
{
    /// <summary>缩略图最长边的像素上限。</summary>
    public const int ThumbnailMaxEdge = 320;

    private readonly string _imageDirectory;
    private readonly ILogger<ImageFileStore> _logger;

    /// <summary>创建图片存储。</summary>
    /// <param name="imageDirectory">图片目录（由 <c>AppPaths</c> 提供）。</param>
    /// <param name="logger">日志。</param>
    public ImageFileStore(string imageDirectory, ILogger<ImageFileStore> logger)
    {
        _imageDirectory = imageDirectory ?? throw new ArgumentNullException(nameof(imageDirectory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ImageSaveResult> SaveAsync(byte[] bytes, string? formatHint = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new ArgumentException("图片内容为空。", nameof(bytes));
        }

        Directory.CreateDirectory(_imageDirectory);

        var probe = ImageProbe.TryReadMetadata(bytes, formatHint);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        var extension = string.IsNullOrWhiteSpace(probe.Format) ? "img" : probe.Format;
        var key = new StorageKey($"{hash}.{extension}");
        var originalPath = Path.Combine(_imageDirectory, key.Value);
        var thumbnailKey = new StorageKey($"{hash}.thumb.png");
        var thumbnailPath = Path.Combine(_imageDirectory, thumbnailKey.Value);

        await File.WriteAllBytesAsync(originalPath, bytes, cancellationToken).ConfigureAwait(false);

        var thumbnailCreated = await TryCreateThumbnailAsync(originalPath, thumbnailPath, cancellationToken).ConfigureAwait(false);
        if (!thumbnailCreated)
        {
            // 缩略图失败不阻断：回退到原图，由 UI 解码时限制尺寸
            thumbnailKey = key;
        }

        _logger.LogDebug("图片已存储：{Key}（{Width}×{Height}，{Bytes} 字节，缩略图 {Thumb}）。",
            key.Value, probe.Width, probe.Height, bytes.Length, thumbnailCreated);

        return new ImageSaveResult(key, thumbnailKey, probe.Width, probe.Height, bytes.LongLength, probe.Format);
    }

    /// <inheritdoc />
    public async Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            return null;
        }

        var path = Path.Combine(_imageDirectory, Path.GetFileName(key.Value));
        if (!File.Exists(path))
        {
            _logger.LogWarning("图片文件缺失：{Path}", path);
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var path = Path.Combine(_imageDirectory, Path.GetFileName(key.Value));
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "删除图片失败：{Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "删除图片被拒绝：{Path}", path);
        }

        return Task.CompletedTask;
    }

    private async Task<bool> TryCreateThumbnailAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(targetPath))
            {
                return true;
            }

            var decoded = await Task.Run(
                () =>
                {
                    var uri = new Uri(sourcePath, UriKind.Absolute);

                    // 第一遍：只读元数据，拿到原始像素尺寸
                    var probe = BitmapFrame.Create(uri, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    var sourceWidth = probe.PixelWidth;
                    var sourceHeight = probe.PixelHeight;

                    // 第二遍：按需限制解码尺寸（原图已小于上限则不再缩放，避免二次插值损失）
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = uri;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                    if (sourceWidth >= sourceHeight && sourceWidth > ThumbnailMaxEdge)
                    {
                        image.DecodePixelWidth = ThumbnailMaxEdge;
                    }
                    else if (sourceHeight > sourceWidth && sourceHeight > ThumbnailMaxEdge)
                    {
                        image.DecodePixelHeight = ThumbnailMaxEdge;
                    }

                    image.EndInit();
                    image.Freeze();
                    return image;
                },
                cancellationToken).ConfigureAwait(false);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(decoded));

            await Task.Run(
                () =>
                {
                    using var output = File.Create(targetPath);
                    encoder.Save(output);
                },
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "生成缩略图失败，将回退使用原图：{Path}", sourcePath);
            return false;
        }
    }
}
