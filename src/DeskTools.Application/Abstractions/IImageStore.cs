using DeskTools.Domain.ValueObjects;

namespace DeskTools.Application.Abstractions;

/// <summary>
/// 图片二进制内容的本地存储（落盘 / 读取 / 删除）。
/// 领域层只持有 <see cref="StorageKey"/>，真实文件 IO 全部在基础设施层。
/// </summary>
public interface IImageStore
{
    /// <summary>保存图片字节，返回存储键与解码后的元数据。</summary>
    Task<ImageSaveResult> SaveAsync(byte[] bytes, string? formatHint = null, CancellationToken cancellationToken = default);

    /// <summary>读取图片字节；文件缺失时返回 <c>null</c>。</summary>
    Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default);

    /// <summary>删除图片；文件不存在视为成功。</summary>
    Task DeleteAsync(StorageKey key, CancellationToken cancellationToken = default);
}

/// <summary>图片保存结果。</summary>
/// <param name="Key">原图存储键。</param>
/// <param name="ThumbnailKey">缩略图存储键（生成失败时与原图相同）。</param>
/// <param name="PixelWidth">像素宽。</param>
/// <param name="PixelHeight">像素高。</param>
/// <param name="SizeBytes">字节数。</param>
/// <param name="Format">格式（png/jpg/…）。</param>
public sealed record ImageSaveResult(
    StorageKey Key,
    StorageKey ThumbnailKey,
    int PixelWidth,
    int PixelHeight,
    long SizeBytes,
    string? Format);
