using DeskShelf.Application.Abstractions;
using DeskShelf.Domain;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using DeskShelf.Infrastructure.Imaging;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Infrastructure.Content;

/// <summary>
/// unit 内容工厂：把归一化的外部内容转换为领域 unit。
/// 这是全应用<b>唯一</b>的 unit 创建入口，新增 unit 类型时只需在此扩展分派逻辑，
/// 用例层与 UI 层无需改动。
/// </summary>
/// <remarks>
/// 路径类内容的处理规则（按实测使用习惯确定）：
/// <list type="bullet">
/// <item>拖入<b>文件夹</b> → 存该文件夹 unit；</item>
/// <item>拖入<b>图片文件</b> → 存为图片 unit（可看缩略图、可复制图片本身）；</item>
/// <item>拖入<b>其它任意文件</b> → 存为文件 unit（双击用默认程序打开）；</item>
/// <item>拖入<b>快捷方式（.lnk）</b> → 先解析目标，再按上述规则处理。</item>
/// </list>
/// 历史说明：引入文件 unit 之前，拖入普通文件会被存成「其所在目录」
/// （当时意图是「我要经常回到这个目录」）。有了文件 unit 后该降级行为已移除 ——
/// 想存目录就直接拖目录，拖单个文件不会再意外带入整个目录。
/// </remarks>
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
        var seenFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedMissing = 0;
        var skippedDuplicateFiles = 0;
        var truncated = paths.Count > MaxDroppedItems;

        foreach (var rawPath in paths.Take(MaxDroppedItems))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            var path = ResolveShortcut(rawPath);

            if (Directory.Exists(path))
            {
                AddFolder(path, content.Origin, units, seenFolders);
                continue;
            }

            if (!File.Exists(path))
            {
                skippedMissing++;
                continue;
            }

            // 图片文件：按图片 unit 处理（可看缩略图、可复制图片本身就是图片、可用看图程序打开）
            var imageUnit = await TryCreateImageUnitFromFileAsync(path, content.Origin, cancellationToken).ConfigureAwait(false);
            if (imageUnit is not null)
            {
                units.Add(imageUnit);
                continue;
            }

            // 其它任意文件：存为文件 unit，双击用系统默认程序打开
            if (!AddFile(path, content.Origin, units, seenFiles))
            {
                skippedDuplicateFiles++;
            }
        }

        if (truncated)
        {
            warnings.Add($"一次最多处理 {MaxDroppedItems} 项，超出部分已忽略");
        }

        if (skippedDuplicateFiles > 0)
        {
            warnings.Add($"{skippedDuplicateFiles} 个重复文件已跳过");
        }

        if (skippedMissing > 0)
        {
            warnings.Add($"{skippedMissing} 个路径已不存在");
        }

        _logger.LogDebug(
            "拖入内容解析完成：{Folders} 个文件夹 / {Images} 个图片 / {Files} 个文件，{Missing} 个失效路径，{Duplicates} 个重复文件。",
            units.Count(u => u.Kind == UnitKind.Folder),
            units.Count(u => u.Kind == UnitKind.Image),
            units.Count(u => u.Kind == UnitKind.File),
            skippedMissing,
            skippedDuplicateFiles);

        return units.Count == 0
            ? UnitCreationResult.Unsupported(warnings.ToArray())
            : warnings.Count == 0
                ? UnitCreationResult.Success(units)
                : UnitCreationResult.Partial(units, warnings);
    }

    /// <summary>加入文件 unit，返回是否为新加入（同一文件只存一次）。</summary>
    private bool AddFile(string path, UnitOrigin origin, List<ShelfUnit> units, HashSet<string> seen)
    {
        var normalized = FileUnit.Normalize(path);
        if (normalized.Length == 0 || !seen.Add(normalized))
        {
            return false;
        }

        units.Add(new FileUnit(normalized, UnitId.New(), origin, _clock()));
        return true;
    }

    /// <summary>加入文件夹 unit，返回是否为新加入（同一目录只存一次）。</summary>
    private bool AddFolder(string path, UnitOrigin origin, List<ShelfUnit> units, HashSet<string> seen)
    {
        var normalized = FolderUnit.Normalize(path);
        if (normalized.Length == 0 || !seen.Add(normalized))
        {
            return false;
        }

        units.Add(new FolderUnit(normalized, UnitId.New(), origin, _clock()));
        return true;
    }

    /// <summary>
    /// 解析快捷方式（.lnk）指向的真实路径；非快捷方式或解析失败时原样返回。
    /// </summary>
    private string ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        try
        {
            // WScript.Shell 是解析 .lnk 最可靠的方式（含相对路径与工作目录）
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return path;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? path : target;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析快捷方式失败，按原路径处理：{Path}", path);
            return path;
        }
    }

    private async Task<ShelfUnit?> TryCreateImageUnitFromFileAsync(string path, UnitOrigin origin, CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            // 快速排除空文件与超大文件，避免逐个尝试解码
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
