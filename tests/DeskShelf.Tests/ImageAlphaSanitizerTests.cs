using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskShelf.Infrastructure.Clipboard;
using DeskShelf.Infrastructure.Imaging;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 「Snipaste 截图粘进面板是全透明」的回归测试。
/// </summary>
/// <remarks>
/// 真实成因（实测）：Snipaste 只往剪贴板放 24bpp / BI_RGB 的 <c>CF_DIB</c>（无 alpha 通道），
/// WPF 把它包装成 <c>Bgra32</c> 的 <see cref="System.Windows.Interop.InteropBitmap"/>，
/// 凭空多出的 alpha 全为 0 —— 直接编码就是 100% 透明的 PNG。
/// 这里用「alpha 全 0 但 RGB 有值」的位图模拟该情形。
/// </remarks>
public sealed class ImageAlphaSanitizerTests
{
    [Fact]
    public void IsFullyTransparent_DetectsZeroAlphaBitmap()
    {
        var bitmap = CreateBitmap(alpha: 0, red: 200, green: 100, blue: 50);

        Assert.True(ImageAlphaSanitizer.IsFullyTransparent(bitmap));
    }

    [Fact]
    public void IsFullyTransparent_ReturnsFalseForOpaqueBitmap()
    {
        var bitmap = CreateBitmap(alpha: 255);

        Assert.False(ImageAlphaSanitizer.IsFullyTransparent(bitmap));
    }

    [Fact]
    public void IsFullyTransparent_ReturnsFalseWhenOnlySomePixelsAreTransparent()
    {
        // 角落里有个透明像素，但整体不是「没有 alpha 信息」
        var bitmap = CreateBitmap(alpha: 255);
        var pixels = ReadPixels(bitmap);
        pixels[3] = 0;
        var mixed = BitmapSource.Create(
            bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, bitmap.PixelWidth * 4);

        Assert.False(ImageAlphaSanitizer.IsFullyTransparent(mixed));
    }

    [Fact]
    public void IsFullyTransparent_ReturnsFalseForFormatWithoutAlphaChannel()
    {
        var pixels = new byte[4 * 4 * 4];
        var bitmap = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgr32, null, pixels, 4 * 4);

        Assert.False(ImageAlphaSanitizer.IsFullyTransparent(bitmap));
    }

    [Fact]
    public void EnsureOpaque_SetsAlphaAndKeepsColors()
    {
        var bitmap = CreateBitmap(alpha: 0, red: 200, green: 100, blue: 50);

        var repaired = ImageAlphaSanitizer.EnsureOpaque(bitmap);

        Assert.False(ImageAlphaSanitizer.IsFullyTransparent(repaired));

        var pixels = ReadPixels(repaired);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(255, pixels[i + 3]);   // A
            Assert.Equal(50, pixels[i]);        // B
            Assert.Equal(100, pixels[i + 1]);   // G
            Assert.Equal(200, pixels[i + 2]);   // R
        }
    }

    [Fact]
    public void EnsureOpaque_ReturnsSameInstanceWhenNothingToFix()
    {
        var bitmap = CreateBitmap(alpha: 255);

        Assert.Same(bitmap, ImageAlphaSanitizer.EnsureOpaque(bitmap));
    }

    [Fact]
    public void EnsureOpaque_ResultIsFrozenSoItCanCrossThreads()
    {
        var repaired = ImageAlphaSanitizer.EnsureOpaque(CreateBitmap(alpha: 0));

        Assert.True(repaired.IsFrozen);
    }

    [Fact]
    public void TryRepairPng_RewritesFullyTransparentPng()
    {
        var brokenPng = EncodePng(CreateBitmap(alpha: 0, red: 200, green: 100, blue: 50));

        var fixedPng = ImageAlphaSanitizer.TryRepairPng(brokenPng);

        Assert.NotNull(fixedPng);
        var decoded = DecodePng(fixedPng!);
        Assert.Equal(4, decoded.PixelWidth);
        Assert.Equal(6, decoded.PixelHeight);
        Assert.False(ImageAlphaSanitizer.IsFullyTransparent(decoded));

        var pixels = ReadPixels(decoded);
        Assert.Equal(255, pixels[3]);
        Assert.Equal(200, pixels[2]);   // 红通道原样保留
    }

    [Fact]
    public void TryRepairPng_LeavesHealthyPngAlone()
    {
        var healthyPng = EncodePng(CreateBitmap(alpha: 255));

        Assert.Null(ImageAlphaSanitizer.TryRepairPng(healthyPng));
    }

    [Fact]
    public void TryRepairPng_LeavesGenuinelyTransparentPngAlone()
    {
        // 有透明区域但并非全透明（例如浏览器复制的透明 PNG）→ 不得改动
        var bitmap = CreateBitmap(alpha: 255);
        var pixels = ReadPixels(bitmap);
        pixels[3] = 0;
        pixels[7] = 0;
        var semi = BitmapSource.Create(
            bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, bitmap.PixelWidth * 4);
        var png = EncodePng(semi);

        Assert.Null(ImageAlphaSanitizer.TryRepairPng(png));
    }

    [Fact]
    public void TryReadImagePng_ProducesVisibleImageFromTransparentClipboardBitmap()
    {
        // 端到端：模拟截图软件放进剪贴板的「alpha 全 0」位图，走真实读取路径
        var transparent = CreateBitmap(alpha: 0, red: 200, green: 100, blue: 50);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, transparent);

        var png = DataObjectReader.TryReadImagePng(data);

        Assert.NotNull(png);
        var decoded = DecodePng(png!);
        Assert.False(
            ImageAlphaSanitizer.IsFullyTransparent(decoded),
            "从剪贴板读到的图片不能是 100% 透明的 —— 否则用户看到的就是一张空白卡片");
    }

    /// <summary>造一张 4×6 的 Bgra32 位图，所有像素同色。</summary>
    private static BitmapSource CreateBitmap(byte alpha, byte red = 0, byte green = 0, byte blue = 0)
    {
        const int width = 4;
        const int height = 6;
        const int stride = width * 4;
        var pixels = new byte[stride * height];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blue;
            pixels[i + 1] = green;
            pixels[i + 2] = red;
            pixels[i + 3] = alpha;
        }

        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapSource DecodePng(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }
}
