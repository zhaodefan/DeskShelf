using DeskShelf.App.ViewModels;
using DeskShelf.Application.Services;
using DeskShelf.Domain.Units;
using DeskShelf.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 右键菜单项的可见性完全由 <c>UnitCapabilities</c> 推导出来。
/// </summary>
/// <remarks>
/// 守护理由：这些属性错了不会崩，但会让菜单缺项或多出无效项 ——
/// 例如给文件夹显示「复制」（点了没反应），或给文本显示「打开」（打开即编辑，语义重复）。
/// 因此按「类型 → 期望菜单」把整张矩阵钉死。
/// </remarks>
public sealed class UnitContextMenuCapabilityTests
{
    private static UnitViewModel CreateFor(ShelfUnit unit, ShelfTestContext ctx)
        => new(unit, ctx.Service, new ImageBytesCache(new FakeImageBytesSource(), NullLogger<ImageBytesCache>.Instance));

    private static string MenuOf(UnitViewModel vm)
    {
        var items = new List<string>();
        if (vm.CanOpen)
        {
            items.Add("打开");
        }

        if (vm.CanEdit)
        {
            items.Add("编辑");
        }

        if (vm.CanCopy)
        {
            items.Add("复制");
        }

        if (vm.CanReveal)
        {
            items.Add("在资源管理器中显示");
        }

        items.Add("移除");   // 总是可用

        return string.Join(" / ", items);
    }

    [Fact]
    public void TextUnit_MenuIsEditCopyRemove()
    {
        var ctx = new ShelfTestContext();
        var vm = CreateFor(
            new TextUnit("内容", Domain.ValueObjects.UnitId.New(), UnitOrigin.Clipboard, DateTimeOffset.UtcNow),
            ctx);

        Assert.Equal("编辑 / 复制 / 移除", MenuOf(vm));
    }

    [Fact]
    public void FolderUnit_MenuIsOpenRevealRemove()
    {
        var ctx = new ShelfTestContext();
        var vm = CreateFor(
            new FolderUnit(@"C:\Windows", Domain.ValueObjects.UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
            ctx);

        // 文件夹按需求不支持复制到剪贴板，因此没有「复制」
        Assert.Equal("打开 / 在资源管理器中显示 / 移除", MenuOf(vm));
    }

    [Fact]
    public void FileUnit_MenuIncludesCopyAndReveal()
    {
        var ctx = new ShelfTestContext();
        var vm = CreateFor(
            new FileUnit(@"C:\Windows\win.ini", Domain.ValueObjects.UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
            ctx);

        Assert.Equal("打开 / 复制 / 在资源管理器中显示 / 移除", MenuOf(vm));
    }

    [Fact]
    public void ImageUnit_MenuIsOpenCopyRevealRemove()
    {
        var ctx = new ShelfTestContext();
        var vm = CreateFor(
            new ImageUnit(
                new Domain.ValueObjects.StorageKey("x.png"),
                10,
                10,
                100,
                "PNG",
                new Domain.ValueObjects.StorageKey("x.png"),
                Domain.ValueObjects.UnitId.New(),
                UnitOrigin.Clipboard,
                DateTimeOffset.UtcNow),
            ctx);

        Assert.Equal("打开 / 复制 / 在资源管理器中显示 / 移除", MenuOf(vm));
    }

    [Fact]
    public void CanOpenAndCanEdit_AreMutuallyExclusive()
    {
        // 文本的「打开」就是「编辑」，两者同时出现会让菜单出现两个等效项
        var ctx = new ShelfTestContext();
        var units = new ShelfUnit[]
        {
            new TextUnit("t", Domain.ValueObjects.UnitId.New(), UnitOrigin.Clipboard, DateTimeOffset.UtcNow),
            new FolderUnit(@"C:\Windows", Domain.ValueObjects.UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
            new FileUnit(@"C:\Windows\win.ini", Domain.ValueObjects.UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow),
        };

        foreach (var unit in units)
        {
            var vm = CreateFor(unit, ctx);
            Assert.False(vm.CanOpen && vm.CanEdit, $"{unit.Kind} 不应同时提供「打开」与「编辑」");
        }
    }
}
