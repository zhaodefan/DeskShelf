using DeskTools.Application.Abstractions;
using DeskTools.Application.Services;
using DeskTools.Domain;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskTools.App.Services;

/// <summary>
/// 设计走查用的样例数据注入（仅当环境变量 <c>DESKTOOLS_SEED_SAMPLE=1</c> 时启用）。
/// </summary>
/// <remarks>
/// 存在的意义：图片与文件夹 unit 只能通过拖拽从外部产生，而「拖拽」无法在自动化环境下
/// 可靠触发，导致 UI 走查时看不到这两类卡片。此通道让视觉验收可以随时复现，
/// 同时因为需要显式环境变量才生效，不会污染普通用户的正常使用。
/// </remarks>
public sealed class SampleDataSeeder
{
    /// <summary>启用开关的环境变量名。</summary>
    public const string EnvironmentVariable = "DESKTOOLS_SEED_SAMPLE";

    /// <summary>样例图片的像素尺寸（由 <c>ImageFileStore</c> 真实生成缩略图）。</summary>
    private const int SampleImageWidth = 640;

    /// <summary>样例图片的像素高度。</summary>
    private const int SampleImageHeight = 400;

    private readonly ShelfService _shelf;
    private readonly IImageStore _images;
    private readonly ILogger<SampleDataSeeder> _logger;

    /// <summary>创建注入器。</summary>
    public SampleDataSeeder(ShelfService shelf, IImageStore images, ILogger<SampleDataSeeder> logger)
    {
        _shelf = shelf ?? throw new ArgumentNullException(nameof(shelf));
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前是否应注入样例数据。</summary>
    public static bool IsEnabled
        => string.Equals(Environment.GetEnvironmentVariable(EnvironmentVariable), "1", StringComparison.Ordinal);

    /// <summary>向面板追加一组覆盖三类 unit 的样例内容。</summary>
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        _logger.LogInformation("检测到 {Variable}=1，注入设计走查样例数据。", EnvironmentVariable);

        await _shelf.IngestAsync(
            RawContent.FromText(
                "这是一条文本 unit：用于确认多行排版、行高与省略号。\n" +
                "第二行用来观察换行是否保留。\n" +
                "第三行用来观察卡片高度上限。",
                UnitOrigin.Clipboard),
            cancellationToken: cancellationToken).ConfigureAwait(true);

        var imageBytes = CreateSamplePng();
        await _shelf.IngestAsync(
            RawContent.FromImage(imageBytes, "png", UnitOrigin.DragDrop),
            cancellationToken: cancellationToken).ConfigureAwait(true);

        await _shelf.IngestAsync(
            RawContent.FromPaths(new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }, UnitOrigin.DragDrop),
            cancellationToken: cancellationToken).ConfigureAwait(true);
    }

    /// <summary>生成一张纯色渐变样例 PNG（避免引入测试图片资源）。</summary>
    private static byte[] CreateSamplePng()
    {
        var stride = SampleImageWidth * 4;
        var pixels = new byte[stride * SampleImageHeight];

        for (var y = 0; y < SampleImageHeight; y++)
        {
            for (var x = 0; x < SampleImageWidth; x++)
            {
                var offset = (y * stride) + (x * 4);
                pixels[offset + 0] = (byte)(40 + (x * 160 / SampleImageWidth));      // B
                pixels[offset + 1] = (byte)(90 + (y * 140 / SampleImageHeight));     // G
                pixels[offset + 2] = (byte)(220 - (x * 90 / SampleImageWidth));      // R
                pixels[offset + 3] = 255;                                            // A
            }
        }

        var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
            SampleImageWidth,
            SampleImageHeight,
            96,
            96,
            System.Windows.Media.PixelFormats.Bgra32,
            palette: null,
            pixels,
            stride);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
