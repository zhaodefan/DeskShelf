using DeskShelf.Application.Catalog;
using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using DeskShelf.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// <c>JsonShelfStore</c> 的往返测试：先落盘再读回，验证每种 unit 类型都能无损持久化。
/// </summary>
/// <remarks>
/// 为什么值得单独测：序列化映射是「新增 unit 类型时最容易漏掉的一处」，
/// 漏了会导致重启后内容消失（且不会抛异常，只是静默少一条记录）。
/// 这里对全部已注册类型做<b>参数化</b>覆盖，新增类型若忘记加映射会自动失败。
/// </remarks>
public sealed class JsonShelfStoreTests : IDisposable
{
    private readonly string _root;
    private readonly AppPaths _paths;

    public JsonShelfStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DeskShelf-store-" + Guid.NewGuid().ToString("N"));
        _paths = new AppPaths(_root);
        _paths.EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private JsonShelfStore CreateStore() => new(_paths, NullLogger<JsonShelfStore>.Instance);

    private static ShelfUnit[] SampleUnits() => new ShelfUnit[]
    {
        new TextUnit("第一行\n第二行", UnitId.New(), UnitOrigin.Clipboard, DateTimeOffset.UtcNow),
        new ImageUnit(
            new StorageKey("abc123.png"),
            640,
            400,
            7340,
            "PNG",
            new StorageKey("abc123.thumb.png"),
            UnitId.New(),
            UnitOrigin.Clipboard,
            DateTimeOffset.UtcNow),
        new FolderUnit(@"C:\Users\Public", UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
        new FileUnit(@"C:\Users\Public\report.pdf", UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
    };

    [Fact]
    public async Task SaveAndLoad_RoundTripsEveryRegisteredKind()
    {
        var store = CreateStore();
        var units = SampleUnits();

        // 先确认样本覆盖了全部已注册类型，否则"全部类型都能往返"这个结论不成立
        var sampledKinds = units.Select(u => u.Kind).Distinct().OrderBy(k => k).ToList();
        var registeredKinds = UnitCatalog.RegisteredKinds.OrderBy(k => k).ToList();
        Assert.Equal(registeredKinds, sampledKinds);

        await store.SaveAsync(units);
        var loaded = await CreateStore().LoadAsync();

        Assert.Equal(units.Length, loaded.Count);

        foreach (var original in units)
        {
            var restored = Assert.Single(loaded, u => u.Id == original.Id);
            Assert.Equal(original.Kind, restored.Kind);
            Assert.Equal(original.ContentFingerprint, restored.ContentFingerprint);
            Assert.Equal(original.Origin, restored.Origin);
        }
    }

    [Fact]
    public async Task SaveAndLoad_PreservesFileUnitPathAndIdentity()
    {
        var store = CreateStore();
        var path = @"C:\Users\Public\季度报告.xlsx";
        var unit = new FileUnit(path, UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow);

        await store.SaveAsync(new[] { unit });
        var restored = Assert.IsType<FileUnit>(Assert.Single(await CreateStore().LoadAsync()));

        Assert.Equal(unit.Path, restored.Path);
        Assert.Equal("季度报告.xlsx", restored.DisplayName);
        Assert.Equal(UnitOpenBehavior.OpenFile, restored.Capabilities.OpenBehavior);
    }

    [Fact]
    public async Task Load_SkipsRecordWithEmptyPath()
    {
        // 手工写入一条缺少 path 的 file 记录：应被跳过而不是整体失败
        await File.WriteAllTextAsync(
            _paths.ShelfFile,
            """
            {
              "SchemaVersion": 1,
              "Units": [
                { "Type": "file", "Id": "11111111-1111-1111-1111-111111111111", "Origin": "DragDrop", "Path": "" },
                { "Type": "file", "Id": "22222222-2222-2222-2222-222222222222", "Origin": "DragDrop", "Path": "C:\\a.txt" }
              ]
            }
            """);

        var loaded = await CreateStore().LoadAsync();

        Assert.Single(loaded);
        Assert.Equal(UnitKind.File, loaded[0].Kind);
    }
}
