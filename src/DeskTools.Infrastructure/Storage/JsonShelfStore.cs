using System.Text.Json;
using System.Text.Json.Serialization;
using DeskTools.Application.Abstractions;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Storage;

/// <summary>
/// 置物架的 JSON 持久化（<c>%APPDATA%\DeskTools\shelf.json</c>）。
/// </summary>
/// <remarks>
/// 设计要点：
/// <list type="bullet">
/// <item>写入使用「临时文件 + 替换」，保证断电/崩溃不会留下半截 JSON；</item>
/// <item>解析失败一律<b>备份</b>为 <c>backup/shelf.&lt;ts&gt;.corrupt.json</c> 并以空面板启动 —— 绝不因数据损坏而启动失败；</item>
/// <item>单条记录损坏时只跳过该条，不影响其余 unit。</item>
/// </list>
/// </remarks>
public sealed class JsonShelfStore : IShelfStore
{
    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<JsonShelfStore> _logger;

    /// <summary>创建仓储。</summary>
    public JsonShelfStore(AppPaths paths, ILogger<JsonShelfStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ShelfUnit>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var file = _paths.ShelfFile;
        if (!File.Exists(file))
        {
            _logger.LogInformation("未找到数据文件，按空面板启动：{File}", file);
            return Array.Empty<ShelfUnit>();
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取数据文件失败：{File}", file);
            return Array.Empty<ShelfUnit>();
        }

        ShelfDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ShelfDocument>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "数据文件解析失败，将备份并以空面板启动。");
            BackupCorruptFile(file);
            return Array.Empty<ShelfUnit>();
        }

        if (document?.Units is null or { Count: 0 })
        {
            return Array.Empty<ShelfUnit>();
        }

        if (document.SchemaVersion > CurrentSchemaVersion)
        {
            _logger.LogWarning("数据文件 schemaVersion={Version} 高于当前支持的 {Current}，将尽力兼容读取。",
                document.SchemaVersion, CurrentSchemaVersion);
        }

        var units = new List<ShelfUnit>(document.Units.Count);
        var skipped = 0;
        foreach (var dto in document.Units)
        {
            var unit = TryMap(dto);
            if (unit is null)
            {
                skipped++;
                continue;
            }

            units.Add(unit);
        }

        _logger.LogInformation("数据文件加载完成：{Count} 个 unit（跳过 {Skipped} 条无效记录）。", units.Count, skipped);
        return units;
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyList<ShelfUnit> units, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(units);

        _paths.EnsureCreated();
        var document = new ShelfDocument
        {
            SchemaVersion = CurrentSchemaVersion,
            Units = units.Select(Map).ToList(),
        };

        var json = JsonSerializer.Serialize(document, WriteOptions);
        await WriteAtomicAsync(_paths.ShelfFile, json, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("已保存 {Count} 个 unit。", units.Count);
    }

    private static async Task WriteAtomicAsync(string targetPath, string content, CancellationToken cancellationToken)
    {
        var directory = System.IO.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = targetPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

        if (File.Exists(targetPath))
        {
            File.Replace(tempPath, targetPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tempPath, targetPath, overwrite: true);
        }
    }

    private void BackupCorruptFile(string file)
    {
        try
        {
            Directory.CreateDirectory(_paths.Backups);
            var backup = System.IO.Path.Combine(_paths.Backups, $"shelf.{DateTime.Now:yyyyMMdd-HHmmss}.corrupt.json");
            File.Copy(file, backup, overwrite: true);
            _logger.LogWarning("损坏的数据文件已备份到：{Backup}", backup);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "备份损坏数据文件失败。");
        }
    }

    private ShelfUnit? TryMap(UnitDto dto)
    {
        try
        {
            var id = UnitId.TryParse(dto.Id) ?? UnitId.New();
            var createdAt = dto.CreatedAt ?? DateTimeOffset.UtcNow;
            var origin = Enum.TryParse<UnitOrigin>(dto.Origin, ignoreCase: true, out var parsed) ? parsed : UnitOrigin.Unknown;

            switch (dto.Type?.ToLowerInvariant())
            {
                case UnitTypeNames.Text:
                    return string.IsNullOrEmpty(dto.Text)
                        ? null
                        : new TextUnit(dto.Text, id, origin, createdAt);

                case UnitTypeNames.Image:
                    return string.IsNullOrWhiteSpace(dto.ImageKey)
                        ? null
                        : new ImageUnit(
                            new StorageKey(dto.ImageKey),
                            dto.PixelWidth ?? 0,
                            dto.PixelHeight ?? 0,
                            dto.SizeBytes ?? 0,
                            dto.Format,
                            string.IsNullOrWhiteSpace(dto.ThumbnailKey) ? new StorageKey(dto.ImageKey) : new StorageKey(dto.ThumbnailKey),
                            id,
                            origin,
                            createdAt);

                case UnitTypeNames.Folder:
                    var path = FolderUnit.Normalize(dto.Path);
                    return path.Length == 0 ? null : new FolderUnit(path, id, origin, createdAt);

                default:
                    _logger.LogWarning("未知的 unit 类型「{Type}」，已跳过（可能来自更高版本）。", dto.Type);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "单条 unit 记录解析失败，已跳过。");
            return null;
        }
    }

    private static UnitDto Map(ShelfUnit unit) => unit switch
    {
        TextUnit text => new UnitDto
        {
            Type = UnitTypeNames.Text,
            Id = text.Id.ToString(),
            Origin = text.Origin.ToString(),
            CreatedAt = text.CreatedAt,
            Text = text.Text,
        },
        ImageUnit image => new UnitDto
        {
            Type = UnitTypeNames.Image,
            Id = image.Id.ToString(),
            Origin = image.Origin.ToString(),
            CreatedAt = image.CreatedAt,
            ImageKey = image.ImageKey.Value,
            ThumbnailKey = image.ThumbnailKey.Value,
            PixelWidth = image.PixelWidth,
            PixelHeight = image.PixelHeight,
            SizeBytes = image.SizeBytes,
            Format = image.Format,
        },
        FolderUnit folder => new UnitDto
        {
            Type = UnitTypeNames.Folder,
            Id = folder.Id.ToString(),
            Origin = folder.Origin.ToString(),
            CreatedAt = folder.CreatedAt,
            Path = folder.Path,
        },
        _ => throw new NotSupportedException(
            $"unit 类型 {unit.Kind} 尚未在 {nameof(JsonShelfStore)} 中登记持久化映射。"),
    };

    /// <summary>磁盘上的判别符常量（与领域枚举解耦，便于将来重命名而不破坏旧文件）。</summary>
    private static class UnitTypeNames
    {
        public const string Text = "text";
        public const string Image = "image";
        public const string Folder = "folder";
    }

    private sealed class ShelfDocument
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;

        public List<UnitDto> Units { get; set; } = new();
    }

    private sealed class UnitDto
    {
        public string? Type { get; set; }

        public string? Id { get; set; }

        public string? Origin { get; set; }

        public DateTimeOffset? CreatedAt { get; set; }

        // —— 文本 ——
        public string? Text { get; set; }

        // —— 图片 ——
        public string? ImageKey { get; set; }

        public string? ThumbnailKey { get; set; }

        public int? PixelWidth { get; set; }

        public int? PixelHeight { get; set; }

        public long? SizeBytes { get; set; }

        public string? Format { get; set; }

        // —— 文件夹 ——
        public string? Path { get; set; }
    }
}
