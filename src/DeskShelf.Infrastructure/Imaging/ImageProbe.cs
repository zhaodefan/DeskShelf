using System.Windows.Media.Imaging;

namespace DeskShelf.Infrastructure.Imaging;

/// <summary>图片元数据探测结果。</summary>
/// <param name="IsImage">是否成功识别为图片。</param>
/// <param name="Width">像素宽。</param>
/// <param name="Height">像素高。</param>
/// <param name="Format">规范化后的格式名（png/jpg/gif/…）。</param>
public readonly record struct ImageProbeResult(bool IsImage, int Width, int Height, string? Format)
{
    /// <summary>识别失败的结果。</summary>
    public static ImageProbeResult NotAnImage { get; } = new(false, 0, 0, null);
}

/// <summary>
/// 基于 WIC 的图片探测工具：只读取容器元数据，不做全量像素解码，
/// 因此可以安全地用于「拖入的任意文件是否为图片」的快速判定。
/// </summary>
public static class ImageProbe
{
    /// <summary>尝试识别字节内容是否为图片，并返回尺寸与格式。</summary>
    public static ImageProbeResult TryReadMetadata(byte[] bytes, string? formatHint = null)
    {
        if (bytes is null || bytes.Length < 8)
        {
            return ImageProbeResult.NotAnImage;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            var format = string.IsNullOrWhiteSpace(formatHint)
                ? NormalizeFormat(decoder.CodecInfo?.FriendlyName)
                : NormalizeFormat(formatHint);

            return frame.PixelWidth <= 0 || frame.PixelHeight <= 0
                ? ImageProbeResult.NotAnImage
                : new ImageProbeResult(true, frame.PixelWidth, frame.PixelHeight, format);
        }
        catch (Exception)
        {
            // WIC 对无法识别的容器会抛出多种异常（FileFormatException / NotSupportedException / ArgumentException 等）
            return ImageProbeResult.NotAnImage;
        }
    }

    /// <summary>把 WIC 的 FriendlyName 或文件扩展名规范化为统一格式名。</summary>
    public static string? NormalizeFormat(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var token = raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return token switch
        {
            "jpeg" => "jpg",
            "tiff" => "tif",
            _ => token,
        };
    }
}
