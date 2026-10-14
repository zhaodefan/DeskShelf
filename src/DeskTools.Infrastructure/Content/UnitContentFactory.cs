using DeskTools.Application.Abstractions;
using DeskTools.Domain;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;
using DeskTools.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Content;

/// <summary>
/// unit 内容工厂：把归一化的外部内容转换为领域 unit。
/// 这是全应用<b>唯一</b>的 unit 创建入口，新增 unit 类型时只需在此扩展分派逻辑，
/// 用例层与 UI 层无需改动。
/// </summary>
public sealed class UnitContentFactory : IUnitContentFactory
{
    /// <summary>拖入内容一次最多处理的文件数，防止误拖整个磁盘目录时卡死。</summary>
    public const int MaxDroppedItems = 50;

    private readonly IImageStore _imageStore;
    private readonly ILogger<UnitContentFactory> _logger;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>创建工厂。</summary>
    public UnitContentFactory(IImageStore imageStore, ILogger<UnitContentFactory> logger, Func<DateTimeOffset>? clock = null)
    {
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public async Task<UnitCreationResult> CreateAsync(RawContent content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        return content.Kind switch
        {
            RawContentKind.Text => CreateFromText(content),
            RawContentKind.Image => await CreateFromImageAsync(content, cancellationToken).ConfigureAwait(false),
            RawContentKind.Paths => await CreateFromPathsAsync(content, cancellationToken).ConfigureAwait(false),
            _ => UnitCreationResult.Unsupported("未知的内容类型。"),
        };
    }

    private UnitCreationResult CreateFromText(RawContent content)
    {
        var text = TextUnit.Normalize(content.Text);
        if (text.Length == 0)
        {
            return UnitCreationResult.Unsupported("剪贴板中的文本为空。");
        }

        var unit = new TextUnit(text, UnitId.New(), content.Origin, _clock());
        return UnitCreationResult.Success(new[] { (ShelfUnit)unit });
    }

    private async Task<UnitCreationResult> CreateFromImageAsync(RawContent content, CancellationToken cancellationToken)
    {
        var bytes = content.ImageBytes;
        if (bytes is null or { Length: 0 })
        {
            return UnitCreationResult.Unsupported("剪贴板中的图片为空。");
        }

        try
        {
            var saved = await _imageStore.SaveAsync(bytes, content.ImageFormat, cancellationToken).ConfigureAwait(false);
            var unit = new ImageUnit(
                saved.Key,
                saved.PixelWidth,
                saved.PixelHeight,
                saved.SizeBytes,
                saved.Format,
                saved.ThumbnailKey,
                UnitId.New(),
                content.Origin,
                _clock());

            return UnitCreationResult.Success(new[] { (ShelfUnit)unit });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存图片失败（{Bytes} 字节）。", bytes.Length);
            return UnitCreationResult.Unsupported("图片保存失败，请重试。");
        }
    }

    private async Task<UnitCreationResult> CreateFromPathsAsync(RawContent content, CancellationToken cancellationToken)
    {
        var paths = content.Paths;
        if (paths.Count == 0)
        {
            return UnitCreationResult.Unsupported("没有可存放的路径。");
        }

        var units = new List<ShelfUnit>();
        var warnings = new List<string>();
        var skippedFiles = 0;
        var skippedMissing = 0;
        var truncated = paths.Count > MaxDroppedItems;

        foreach (var rawPath in paths.Take(MaxDroppedItems))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var path = FolderUnit.Normalize(rawPath);
            if (path.Length == 0)
            {
                continue;
            }

            if (Directory.Exists(path))
            {
                units.Add(new FolderUnit(path, UnitId.New(), content.Origin, _clock()));
                continue;
            }

            if (!File.Exists(path))
            {
                skippedMissing++;
                continue;
            }

            // 单个图片文件：按图片 unit 处理（可复制图片、可用看图程序打开）
            var imageUnit = await TryCreateImageUnitFromFileAsync(path, content.Origin, cancellationToken).ConfigureAwait(false);
            if (imageUnit is not null)
            {
                units.Add(imageUnit);
                continue;
            }

            skippedFiles++;
        }

        if (truncated)
        {
            warnings.Add($"一次最多处理 {MaxDroppedItems} 项，超出部分已忽略");
        }

        if (skippedFiles > 0)
        {
            warnings.Add($"{skippedFiles} 个文件不是图片，暂不支持存放（可先拖入其所在文件夹）");
        }

        if (skippedMissing > 0)
        {
            warnings.Add($"{skippedMissing} 个路径已不存在");
        }

        _logger.LogDebug("拖入内容解析完成：{Folders} 个文件夹 / {Images} 个图片，跳过 {Files} 个文件、{Missing} 个失效路径。",
            units.Count(u => u.Kind == UnitKind.Folder),
            units.Count(u => u.Kind == UnitKind.Image),
            skippedFiles,
            skippedMissing);

        return units.Count == 0
            ? UnitCreationResult.Unsupported(warnings.ToArray())
            : warnings.Count == 0
                ? UnitCreationResult.Success(units)
                : UnitCreationResult.Partial(units, warnings);
    }

    private async Task<ShelfUnit?> TryCreateImageUnitFromFileAsync(string path, UnitOrigin origin, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            // 快速排除明显不是图片的常见文档类型，避免逐个尝试解码大文件
            if (info.Length == 0 || info.Length > 256L * 1024 * 1024)
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var probe = ImageProbe.TryReadMetadata(bytes, Path.GetExtension(path).TrimStart('.'));
            if (!probe.IsImage)
            {
                return null;
            }

            var saved = await _imageStore.SaveAsync(bytes, probe.Format, cancellationToken).ConfigureAwait(false);
            return new ImageUnit(
                saved.Key,
                saved.PixelWidth,
                saved.PixelHeight,
                saved.SizeBytes,
                saved.Format,
                saved.ThumbnailKey,
                UnitId.New(),
                origin,
                _clock());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法把文件作为图片处理：{Path}", path);
            return null;
        }
    }
}
