using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Domain.Units;

/// <summary>
/// 图片 unit：面板内展示缩略图，选中后 Ctrl+C 复制图片，双击用系统默认看图程序打开。
/// 领域层只持有 <see cref="StorageKey"/>，真实字节与落盘由基础设施层负责。
/// </summary>
public sealed class ImageUnit : ShelfUnit
{
    /// <summary>新建图片 unit。</summary>
    public ImageUnit(
        StorageKey imageKey,
        int pixelWidth,
        int pixelHeight,
        long sizeBytes,
        string? format,
        StorageKey thumbnailKey,
        UnitId id,
        UnitOrigin origin,
        DateTimeOffset createdAt)
        : base(id, origin, createdAt)
    {
        ImageKey = imageKey;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        SizeBytes = sizeBytes;
        Format = format;
        ThumbnailKey = thumbnailKey;
    }

    /// <inheritdoc />
    public override UnitKind Kind => UnitKind.Image;

    /// <inheritdoc />
    public override UnitCapabilities Capabilities { get; } =
        new(CanCopyToClipboard: true, CanEditText: false, OpenBehavior: UnitOpenBehavior.OpenImage);

    /// <summary>原图在本地图片目录中的键。</summary>
    public StorageKey ImageKey { get; }

    /// <summary>缩略图键（供面板快速渲染）；为空时由 UI 解码原图并缓存。</summary>
    public StorageKey ThumbnailKey { get; internal set; }

    /// <summary>像素宽。</summary>
    public int PixelWidth { get; }

    /// <summary>像素高。</summary>
    public int PixelHeight { get; }

    /// <summary>原始字节数。</summary>
    public long SizeBytes { get; }

    /// <summary>图片格式（<c>png</c> / <c>jpg</c> / …）。</summary>
    public string? Format { get; }

    /// <inheritdoc />
    public override string ContentFingerprint => ImageKey.Value;

    /// <inheritdoc />
    public override string ContentSummary => $"图片 {PixelWidth}×{PixelHeight}，{FormatDisplay}";

    /// <summary>人类可读的格式描述。</summary>
    public string FormatDisplay => string.IsNullOrWhiteSpace(Format) ? "未知格式" : Format.ToUpperInvariant();

    /// <summary>宽高比（用于 ui 占位，避免加载时布局跳动）；非法尺寸返回 1。</summary>
    public double AspectRatio => PixelWidth > 0 && PixelHeight > 0
        ? (double)PixelWidth / PixelHeight
        : 1d;
}
