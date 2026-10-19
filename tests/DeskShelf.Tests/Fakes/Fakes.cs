using DeskShelf.Application.Abstractions;
using DeskShelf.Application.Services;
using DeskShelf.Application.Settings;
using DeskShelf.Application.Undo;
using DeskShelf.Domain;
using DeskShelf.Domain.Shelf;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using DeskShelf.Infrastructure.Content;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskShelf.Tests.Fakes;

/// <summary>内存剪贴板：记录最后一次写入，并可预设待读取内容。</summary>
public sealed class FakeClipboardService : IClipboardService
{
    /// <summary>下一次读取返回的内容。</summary>
    public RawContent? Pending { get; set; }

    /// <summary>最后一次写入的内容。</summary>
    public RawContent? LastWritten { get; private set; }

    /// <summary>写入次数。</summary>
    public int WriteCount { get; private set; }

    /// <summary>是否让写入抛出异常（模拟剪贴板被占用）。</summary>
    public bool FailOnWrite { get; set; }

    /// <inheritdoc />
    public Task<RawContent?> TryReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Pending);

    /// <inheritdoc />
    public Task WriteAsync(RawContent content, CancellationToken cancellationToken = default)
    {
        if (FailOnWrite)
        {
            throw new InvalidOperationException("剪贴板被占用（模拟）。");
        }

        LastWritten = content;
        WriteCount++;
        return Task.CompletedTask;
    }
}

/// <summary>内存图片存储：保留字节并返回确定性的键与尺寸。</summary>
public sealed class FakeImageStore : IImageStore
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private int _counter;

    /// <summary>返回的像素宽。</summary>
    public int Width { get; set; } = 640;

    /// <summary>返回的像素高。</summary>
    public int Height { get; set; } = 480;

    /// <summary>已保存的文件数。</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public Task<ImageSaveResult> SaveAsync(byte[] bytes, string? formatHint = null, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        var key = new StorageKey($"img{++_counter}.png");
        _files[key.Value] = bytes;
        return Task.FromResult(new ImageSaveResult(
            key,
            new StorageKey($"img{_counter}.thumb.png"),
            Width,
            Height,
            bytes.LongLength,
            formatHint ?? "png"));
    }

    /// <inheritdoc />
    public Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default)
        => Task.FromResult(_files.TryGetValue(key.Value, out var bytes) ? bytes : null);

    /// <inheritdoc />
    public Task DeleteAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        _files.Remove(key.Value);
        return Task.CompletedTask;
    }
}

/// <summary>内存置物架仓储。</summary>
public sealed class FakeShelfStore : IShelfStore
{
    /// <summary>当前内容。</summary>
    public List<ShelfUnit> Units { get; } = new();

    /// <summary>保存次数。</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<ShelfUnit>> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ShelfUnit>>(Units.ToList());

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<ShelfUnit> units, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Units.Clear();
        Units.AddRange(units);
        return Task.CompletedTask;
    }
}

/// <summary>内存设置存储。</summary>
public sealed class FakeSettingsStore : ISettingsStore
{
    /// <inheritdoc />
    public ShelfSettings Current { get; private set; } = ShelfSettings.Default;

    /// <inheritdoc />
    public event EventHandler<ShelfSettings>? Changed;

    /// <summary>保存次数。</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public Task<ShelfSettings> LoadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Current);

    /// <inheritdoc />
    public Task SaveAsync(ShelfSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Current = ShelfSettingsValidator.Normalize(settings);
        Changed?.Invoke(this, Current);
        return Task.CompletedTask;
    }
}

/// <summary>记录调用的启动器。</summary>
public sealed class FakeSystemLauncher : ISystemLauncher
{
    /// <summary>被打开过的 unit。</summary>
    public List<ShelfUnit> Opened { get; } = new();

    /// <summary>被定位过的 unit。</summary>
    public List<ShelfUnit> Revealed { get; } = new();

    /// <inheritdoc />
    public Task<bool> OpenAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        Opened.Add(unit);
        return Task.FromResult(unit.Capabilities.OpenBehavior != UnitOpenBehavior.None);
    }

    /// <inheritdoc />
    public Task<bool> RevealInExplorerAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        Revealed.Add(unit);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default)
        => Task.FromResult(true);
}

/// <summary>测试用的服务组装器：把 <c>ShelfService</c> 与全部假实现装在一起。</summary>
public sealed class ShelfTestContext
{
    /// <summary>构造测试上下文。</summary>
    public ShelfTestContext()
    {
        Shelf = new Shelf();
        Clipboard = new FakeClipboardService();
        Images = new FakeImageStore();
        Store = new FakeShelfStore();
        Settings = new FakeSettingsStore();
        Launcher = new FakeSystemLauncher();
        Factory = new UnitContentFactory(Images, NullLogger<UnitContentFactory>.Instance);
        Service = new ShelfService(
            Shelf,
            Factory,
            Clipboard,
            Launcher,
            Store,
            Settings,
            Images,
            new UndoStack(),
            NullLogger<ShelfService>.Instance);
    }

    /// <summary>领域聚合。</summary>
    public Shelf Shelf { get; }

    /// <summary>假剪贴板。</summary>
    public FakeClipboardService Clipboard { get; }

    /// <summary>假图片存储。</summary>
    public FakeImageStore Images { get; }

    /// <summary>假仓储。</summary>
    public FakeShelfStore Store { get; }

    /// <summary>假设置。</summary>
    public FakeSettingsStore Settings { get; }

    /// <summary>假启动器。</summary>
    public FakeSystemLauncher Launcher { get; }

    /// <summary>真实的内容工厂（配合假图片存储）。</summary>
    public UnitContentFactory Factory { get; }

    /// <summary>被测服务。</summary>
    public ShelfService Service { get; }
}
/// <summary>返回固定字节的图片字节源（测试用，仅需满足构造依赖）。</summary>
public sealed class FakeImageBytesSource : IImageBytesSource
{
    /// <summary>读取时返回的字节；默认空数组。</summary>
    public byte[]? Bytes { get; set; } = Array.Empty<byte>();

    /// <summary>读取次数。</summary>
    public int ReadCount { get; private set; }

    /// <inheritdoc />
    public Task<byte[]?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        ReadCount++;
        return Task.FromResult(Bytes);
    }
}