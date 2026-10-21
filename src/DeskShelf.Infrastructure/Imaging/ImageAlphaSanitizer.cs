using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DeskShelf.Infrastructure.Imaging;

/// <summary>
/// 修正「整幅图 alpha 全为 0」的位图 —— 这种图在任何查看器里都是完全空白的。
/// </summary>
/// <remarks>
/// <para><b>为什么会有这种图</b>：从剪贴板取图时，WPF 会把位图包装成
/// <see cref="System.Windows.Interop.InteropBitmap"/>（像素格式 <c>Bgra32</c>）。
/// 若来源应用放进剪贴板的是<b>不带 alpha 通道</b>的 DIB —— 实测 Snipaste 截图后
/// 剪贴板里是 24bpp / BI_RGB 的 <c>CF_DIB</c> + <c>CF_BITMAP</c> + <c>CF_DIBV5</c>，
/// <b>没有 PNG 自定义格式</b> —— 那么这个凭空多出来的 alpha 通道会全是 0，
/// 编码成 PNG 就是一张 100% 透明的图（用户在面板里看到「粘进去是空的」）。</para>
/// <para><b>为什么可以安全地修</b>：实测这类图的 RGB 通道完好（非黑像素 100%、峰值 255），
/// 丢的只是 alpha 信息。因此把「alpha 全 0」解释为「没有 alpha 信息」，
/// 保留 RGB 并把 alpha 全部置 255 即可还原原图。</para>
/// <para><b>为什么只在全 0 时才动</b>：真正带透明区域的图（例如从浏览器复制的透明 PNG）
/// alpha 不可能全为 0，因此不会被误伤。</para>
/// <para><b>已知限制</b>：若来源是预乘格式（<c>Pbgra32</c>）且 alpha 全 0，
/// 其 RGB 在预乘时已被清零、无法还原，修复结果会是纯黑而不是原图。</para>
/// </remarks>
public static class ImageAlphaSanitizer
{
    /// <summary>判断位图是否「所有像素的 alpha 都是 0」（即完全没有 alpha 信息）。</summary>
    /// <param name="source">待判断的位图。</param>
    /// <returns>整幅图 alpha 全为 0 时返回 <c>true</c>；无 alpha 通道时返回 <c>false</c>。</returns>
    public static bool IsFullyTransparent(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Format != PixelFormats.Bgra32 && source.Format != PixelFormats.Pbgra32)
        {
            // Bgr32 / Rgb24 等格式没有 alpha 通道，不存在「全透明」这回事
            return false;
        }

        var (pixels, stride) = CopyPixels(source);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>若位图 alpha 全为 0，返回一份 alpha 全 255 的副本；否则原样返回。</summary>
    /// <param name="source">原始位图。</param>
    /// <returns>可安全显示与编码的位图（已冻结，可跨线程使用）。</returns>
    public static BitmapSource EnsureOpaque(BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!IsFullyTransparent(source))
        {
            return source;
        }

        // 预乘格式先转成直通 alpha，保证 CopyPixels 拿到的 RGB 是原色
        var straight = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var (pixels, stride) = CopyPixels(straight);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }

        var repaired = BitmapSource.Create(
            straight.PixelWidth,
            straight.PixelHeight,
            source.DpiX > 0 ? source.DpiX : 96,
            source.DpiY > 0 ? source.DpiY : 96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        repaired.Freeze();
        return repaired;
    }

    /// <summary>
    /// 检查一段 PNG 字节，若其 alpha 全为 0 则重新编码为不透明 PNG。
    /// </summary>
    /// <param name="pngBytes">PNG 文件字节。</param>
    /// <returns>修复后的 PNG 字节；无需修复时返回 <c>null</c>。</returns>
    public static byte[]? TryRepairPng(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        if (pngBytes.Length == 0)
        {
            return null;
        }

        BitmapFrame frame;
        using (var input = new MemoryStream(pngBytes, writable: false))
        {
            frame = BitmapFrame.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        }

        if (!IsFullyTransparent(frame))
        {
            return null;
        }

        var opaque = EnsureOpaque(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(opaque));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static (byte[] Pixels, int Stride) CopyPixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[(long)stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }
}
