using DeskTools.Domain;
using DeskTools.Domain.Units;
using DeskTools.Domain.ValueObjects;
using DeskTools.Tests.Fakes;
using Xunit;

namespace DeskTools.Tests;

/// <summary>用例层行为测试：覆盖剪贴板摄取、多选复制语义、删除与撤销。</summary>
public sealed class ShelfServiceTests
{
    [Fact]
    public async Task IngestFromClipboard_Text_AppendsUnitAtEnd()
    {
        var ctx = new ShelfTestContext();
        ctx.Clipboard.Pending = RawContent.FromText("第一行\n第二行", UnitOrigin.Clipboard);

        var result = await ctx.Service.IngestFromClipboardAsync();

        Assert.True(result.Success);
        var unit = Assert.IsType<TextUnit>(Assert.Single(ctx.Shelf.Units));
        Assert.Equal("第一行\n第二行", unit.Text);
        Assert.Equal(UnitOrigin.Clipboard, unit.Origin);
    }

    [Fact]
    public async Task Ingest_MultipleTimes_KeepsInsertionOrder()
    {
        var ctx = new ShelfTestContext();

        ctx.Clipboard.Pending = RawContent.FromText("A");
        await ctx.Service.IngestFromClipboardAsync();
        ctx.Clipboard.Pending = RawContent.FromText("B");
        await ctx.Service.IngestFromClipboardAsync();
        ctx.Clipboard.Pending = RawContent.FromText("C");
        await ctx.Service.IngestFromClipboardAsync();

        // 已确认策略：新 unit 追加到末尾（先到的先看到）
        Assert.Equal(new[] { "A", "B", "C" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));
    }

    [Fact]
    public async Task IngestFromClipboard_EmptyClipboard_ReturnsMessageWithoutUnit()
    {
        var ctx = new ShelfTestContext();
        ctx.Clipboard.Pending = null;

        var result = await ctx.Service.IngestFromClipboardAsync();

        Assert.False(result.Success);
        Assert.Empty(ctx.Shelf.Units);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Ingest_Image_StoresBytesAndRecordsMetadata()
    {
        var ctx = new ShelfTestContext();
        ctx.Images.Width = 800;
        ctx.Images.Height = 600;
        ctx.Clipboard.Pending = RawContent.FromImage(new byte[] { 1, 2, 3, 4 }, "png", UnitOrigin.Clipboard);

        var result = await ctx.Service.IngestFromClipboardAsync();

        Assert.True(result.Success);
        var unit = Assert.IsType<ImageUnit>(Assert.Single(ctx.Shelf.Units));
        Assert.Equal(800, unit.PixelWidth);
        Assert.Equal(600, unit.PixelHeight);
        Assert.Equal(1, ctx.Images.SaveCount);
        Assert.False(unit.ThumbnailKey.IsEmpty);
    }

    [Fact]
    public async Task Ingest_Paths_CreatesFolderUnitsAndSkipsMissingPaths()
    {
        var ctx = new ShelfTestContext();
        var existing = Path.GetTempPath();
        var missing = Path.Combine(existing, "desktools-definitely-missing-" + Guid.NewGuid().ToString("N"));

        var result = await ctx.Service.IngestAsync(RawContent.FromPaths(new[] { existing, missing }, UnitOrigin.DragDrop));

        Assert.True(result.Success);
        var unit = Assert.IsType<FolderUnit>(Assert.Single(ctx.Shelf.Units));
        Assert.Equal(FolderUnit.Normalize(existing), unit.Path);
        Assert.Contains("不存在", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ingest_ExceedingLimit_RejectsWithMessage()
    {
        var ctx = new ShelfTestContext();
        await ctx.Settings.SaveAsync(ctx.Settings.Current with { MaxUnits = 20 });

        for (var i = 0; i < 20; i++)
        {
            ctx.Clipboard.Pending = RawContent.FromText($"item-{i}");
            await ctx.Service.IngestFromClipboardAsync();
        }

        ctx.Clipboard.Pending = RawContent.FromText("overflow");
        var result = await ctx.Service.IngestFromClipboardAsync();

        Assert.False(result.Success);
        Assert.Equal(20, ctx.Shelf.Count);
        Assert.Contains("上限", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_SingleTextUnit_WritesPlainText()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "要复制的文本");

        var result = await ctx.Service.CopyAsync(new[] { ctx.Shelf.Units[0] });

        Assert.True(result.Success);
        Assert.Equal("要复制的文本", ctx.Clipboard.LastWritten?.Text);
    }

    [Fact]
    public async Task Copy_MultipleTextUnits_JoinsWithNewline()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "A");
        await AddTextAsync(ctx, "B");

        var result = await ctx.Service.CopyAsync(ctx.Shelf.Units.ToList());

        Assert.True(result.Success);
        Assert.Equal("A\nB", ctx.Clipboard.LastWritten?.Text);
        Assert.Contains("合并", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_MixedTypes_CopiesFirstItemAndExplains()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "文本项");
        await AddTextAsync(ctx, "另一个文本项");
        ctx.Clipboard.Pending = RawContent.FromImage(new byte[] { 9, 9 }, "png", UnitOrigin.Clipboard);
        await ctx.Service.IngestFromClipboardAsync();

        var result = await ctx.Service.CopyAsync(ctx.Shelf.Units.ToList());

        Assert.True(result.Success);
        Assert.Equal("文本项", ctx.Clipboard.LastWritten?.Text);
        Assert.Contains("混合类型", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_FolderUnits_IsRejectedByCapability()
    {
        // 需求明确：文件夹 unit「复制粘贴是无用的」，仅能用于打开路径。
        // 因此能力声明为不可复制，多选复制时也应被跳过。
        var ctx = new ShelfTestContext();
        var folder = Path.GetTempPath();
        await ctx.Service.IngestAsync(RawContent.FromPaths(new[] { folder }, UnitOrigin.DragDrop));

        var result = await ctx.Service.CopyAsync(new[] { ctx.Shelf.Units[0] });

        Assert.False(result.Success);
        Assert.Contains("不支持复制", result.Message, StringComparison.Ordinal);
        Assert.Null(ctx.Clipboard.LastWritten);
    }

    [Fact]
    public async Task Copy_TextAndFolder_SkipsFolderAndCopiesText()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "文本项");
        var folder = Path.GetTempPath();
        await ctx.Service.IngestAsync(RawContent.FromPaths(new[] { folder }, UnitOrigin.DragDrop));

        var result = await ctx.Service.CopyAsync(ctx.Shelf.Units.ToList());

        Assert.True(result.Success);
        Assert.Equal("文本项", ctx.Clipboard.LastWritten?.Text);
        Assert.Contains("已跳过", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_ImageUnit_ReadsOriginalBytes()
    {
        var ctx = new ShelfTestContext();
        var bytes = new byte[] { 7, 7, 7, 7, 7 };
        ctx.Clipboard.Pending = RawContent.FromImage(bytes, "png", UnitOrigin.Clipboard);
        await ctx.Service.IngestFromClipboardAsync();

        var result = await ctx.Service.CopyAsync(new[] { ctx.Shelf.Units[0] });

        Assert.True(result.Success);
        Assert.Equal(RawContentKind.Image, ctx.Clipboard.LastWritten?.Kind);
        Assert.Equal(bytes, ctx.Clipboard.LastWritten!.ImageBytes);
    }

    [Fact]
    public async Task Copy_WhenClipboardBusy_ReturnsFailure()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "x");
        ctx.Clipboard.FailOnWrite = true;

        var result = await ctx.Service.CopyAsync(new[] { ctx.Shelf.Units[0] });

        Assert.False(result.Success);
        Assert.Contains("失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_ThenUndo_RestoresOriginalPosition()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "A");
        await AddTextAsync(ctx, "B");
        await AddTextAsync(ctx, "C");

        var middle = ctx.Shelf.Units[1];
        var removed = ctx.Service.Remove(new[] { middle.Id });

        Assert.Equal(1, removed.RemovedCount);
        Assert.Equal(new[] { "A", "C" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));

        var undone = ctx.Service.Undo();

        Assert.Equal(new[] { "A", "B", "C" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));
        Assert.Contains("撤销", undone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_MultipleNonAdjacentUnits_UndoRestoresExactOrder()
    {
        var ctx = new ShelfTestContext();
        foreach (var text in new[] { "A", "B", "C", "D", "E" })
        {
            await AddTextAsync(ctx, text);
        }

        var ids = new[] { ctx.Shelf.Units[0].Id, ctx.Shelf.Units[2].Id, ctx.Shelf.Units[4].Id };
        ctx.Service.Remove(ids);

        Assert.Equal(new[] { "B", "D" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));

        ctx.Service.Undo();

        Assert.Equal(new[] { "A", "B", "C", "D", "E" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));
    }

    [Fact]
    public async Task ClearAll_ThenUndo_RestoresEverything()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "A");
        await AddTextAsync(ctx, "B");

        var cleared = ctx.Service.ClearAll();
        Assert.Equal(2, cleared.RemovedCount);
        Assert.Empty(ctx.Shelf.Units);

        ctx.Service.Undo();
        Assert.Equal(2, ctx.Shelf.Count);
    }

    [Fact]
    public async Task UpdateText_ChangesContentAndReportsNoChangeForIdenticalText()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "旧内容");
        var id = ctx.Shelf.Units[0].Id;

        Assert.True(ctx.Service.UpdateText(id, "新内容\r\n第二行"));
        var unit = Assert.IsType<TextUnit>(ctx.Shelf.Units[0]);

        // 换行统一为 \n，首尾空白被裁剪
        Assert.Equal("新内容\n第二行", unit.Text);
        Assert.False(ctx.Service.UpdateText(id, "新内容\n第二行"));
    }

    [Fact]
    public async Task Move_ReordersUnits()
    {
        var ctx = new ShelfTestContext();
        await AddTextAsync(ctx, "A");
        await AddTextAsync(ctx, "B");
        await AddTextAsync(ctx, "C");

        Assert.True(ctx.Service.Move(ctx.Shelf.Units[0].Id, 2));
        Assert.Equal(new[] { "B", "C", "A" }, ctx.Shelf.Units.Cast<TextUnit>().Select(u => u.Text));
    }

    [Fact]
    public async Task LoadAsync_UsesStore()
    {
        var ctx = new ShelfTestContext();
        ctx.Store.Units.Add(new TextUnit("来自磁盘", UnitId.New(), UnitOrigin.Unknown, DateTimeOffset.UtcNow));

        var count = await ctx.Service.LoadAsync();

        Assert.Equal(1, count);
        Assert.Equal("来自磁盘", Assert.IsType<TextUnit>(ctx.Shelf.Units[0]).Text);
    }

    private static async Task AddTextAsync(ShelfTestContext ctx, string text)
    {
        ctx.Clipboard.Pending = RawContent.FromText(text, UnitOrigin.Clipboard);
        await ctx.Service.IngestFromClipboardAsync();
    }
}
