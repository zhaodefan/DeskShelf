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
/// <item>拖入<b>文件夹</b> → 直接存该文件夹；</item>
/// <item>拖入<b>图片文件</b> → 存为图片 unit；</item>
/// <item>拖入<b>其它文件</b> → 存其<b>所在目录</b>（用户意图通常是「我要经常回到这个目录」）；</item>
/// <item>拖入<b>快捷方式（.lnk）</b> → 先解析目标，再按上述规则处理。</item>
/// </list>
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
        var skippedMissing = 0;
        var fromFileFolders = 0;
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

            // 单个图片文件：按图片 unit 处理（可复制图片、可用看图程序打开）
            var imageUnit = await TryCreateImageUnitFromFileAsync(path, content.Origin, cancellationToken).ConfigureAwait(false);
            if (imageUnit is not null)
            {
                units.Add(imageUnit);
                continue;
            }

            // 其它文件：用户拖文件的意图通常是「我要经常回到这个目录」，因此存其所在目录
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                if (AddFolder(parent, content.Origin, units, seenFolders))
                {
                    fromFileFolders++;
                }

                continue;
            }

            skippedMissing++;
        }

        if (truncated)
        {
            warnings.Add($"一次最多处理 {MaxDroppedItems} 项，超出部分已忽略");
        }

        if (fromFileFolders > 0)
        {
            warnings.Add($"{fromFileFolders} 项存放的是「文件所在目录」（非图片文件本身不存放）");
        }

        if (skippedMissing > 0)
        {
            warnings.Add($"{skippedMissing} 个路径已不存在");
        }

        _logger.LogDebug(
            "拖入内容解析完成：{Folders} 个文件夹（其中 {FromFiles} 个来自文件所在目录）/ {Images} 个图片，{Missing} 个失效路径。",
            units.Count(u => u.Kind == UnitKind.Folder),
            fromFileFolders,
            units.Count(u => u.Kind == UnitKind.Image),
            skippedMissing);

        return units.Count == 0
            ? UnitCreationResult.Unsupported(warnings.ToArray())
            : warnings.Count == 0
                ? UnitCreationResult.Success(units)
                : UnitCreationResult.Partial(units, warnings);
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
