using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Application.Abstractions;

/// <summary>
/// 面板渲染所需的图片字节读取。
/// 与 <see cref="IImageStore"/> 的区别：这里读的是<b>缩略图优先</b>的展示用数据，
/// 未找到缩略图时由实现回退到原图，避免表现层关心文件布局。
/// </summary>
public interface IImageBytesSource
{
    /// <summary>读取指定键的展示用图片字节；文件缺失时返回 <c>null</c>。</summary>
    Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default);
}
