using System.Text.Json;
using System.Text.Json.Serialization;
using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DeskShelf.Infrastructure.Storage;

/// <summary>
/// 设置持久化（<c>%APPDATA%\DeskShelf\settings.json</c>）。
/// 读取时一律经 <see cref="ShelfSettingsValidator.Normalize"/>，保证下游永远拿到合法值。
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly AppPaths _paths;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>创建设置存储。</summary>
    public JsonSettingsStore(AppPaths paths, ILogger<JsonSettingsStore> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Current = ShelfSettings.Default;
    }

    /// <inheritdoc />
    public ShelfSettings Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<ShelfSettings>? Changed;

    /// <inheritdoc />
    public async Task<ShelfSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var file = _paths.SettingsFile;
        if (!File.Exists(file))
        {
            _logger.LogInformation("未找到设置文件，使用默认设置：{File}", file);
            Update(ShelfSettings.Default);
            return Current;
        }

        try
        {
            var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<ShelfSettings>(json, ReadOptions);
            Update(ShelfSettingsValidator.Normalize(loaded));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "设置文件读取失败，回退到默认设置：{File}", file);
            Update(ShelfSettings.Default);
        }

        return Current;
    }

    /// <inheritdoc />
    public async Task SaveAsync(ShelfSettings settings, CancellationToken cancellationToken = default)
    {
        var normalized = ShelfSettingsValidator.Normalize(settings);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureCreated();
            var json = JsonSerializer.Serialize(normalized, WriteOptions);
            var tempPath = _paths.SettingsFile + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

            if (File.Exists(_paths.SettingsFile))
            {
                File.Replace(tempPath, _paths.SettingsFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _paths.SettingsFile, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "设置保存失败。");
        }
        finally
        {
            _writeLock.Release();
        }

        Update(normalized);
    }

    private void Update(ShelfSettings settings)
    {
        var changed = !Equals(Current, settings);
        Current = settings;
        if (changed)
        {
            Changed?.Invoke(this, settings);
        }
    }
}
