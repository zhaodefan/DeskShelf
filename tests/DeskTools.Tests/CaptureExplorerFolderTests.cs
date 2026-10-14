using DeskTools.Application.Abstractions;
using DeskTools.Application.Services;
using DeskTools.Domain.Units;
using DeskTools.Tests.Fakes;
using Xunit;

namespace DeskTools.Tests;

/// <summary>
/// 「抓取资源管理器当前目录」功能测试。
/// 存在的原因：拖地址栏 / 拖标题栏是否可拖由 Explorer 决定，应用无法保证，
/// 因此需要一个不依赖拖拽的可靠通道。
/// </summary>
public sealed class CaptureExplorerFolderTests
{
    [Fact]
    public void TryConvertToLocalPath_ConvertsDriveAndUnicodeAndSpaces()
    {
        Assert.Equal(@"C:\project\tools", ShelfService.TryConvertToLocalPath("file:///C:/project/tools"));
        Assert.Equal(@"D:\我的 文档\资料", ShelfService.TryConvertToLocalPath("file:///D:/%E6%88%91%E7%9A%84%20%E6%96%87%E6%A1%A3/%E8%B5%84%E6%96%99"));
        Assert.Equal(@"C:\", ShelfService.TryConvertToLocalPath("file:///C:/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.com")]
    [InlineData("shell:MyComputerFolder")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}")]
    public void TryConvertToLocalPath_RejectsNonFileLocations(string url)
    {
        Assert.Null(ShelfService.TryConvertToLocalPath(url));
    }

    [Fact]
    public async Task Capture_WithOpenWindow_AddsFolderUnitForThatDirectory()
    {
        var ctx = new ShelfTestContext();
        var folder = Path.GetTempPath();
        ctx.ExplorerFolders.Locations.Add(new ExplorerWindowInfo($"file:///{folder.Replace('\\', '/')}", "Temp"));

        var result = await ctx.Service.CaptureExplorerFolderAsync();

        Assert.True(result.Success);
        var unit = Assert.IsType<FolderUnit>(Assert.Single(ctx.Shelf.Units));
        Assert.Equal(FolderUnit.Normalize(folder), unit.Path);
        Assert.Contains("资源管理器当前目录", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_UsesMostRecentlyActiveWindowOnly()
    {
        var ctx = new ShelfTestContext();
        var first = Path.GetTempPath();
        var second = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // Shell 返回顺序即「最近激活优先」
        ctx.ExplorerFolders.Locations.Add(new ExplorerWindowInfo($"file:///{first.Replace('\\', '/')}", "Temp"));
        ctx.ExplorerFolders.Locations.Add(new ExplorerWindowInfo($"file:///{second.Replace('\\', '/')}", "Home"));

        await ctx.Service.CaptureExplorerFolderAsync();

        var unit = Assert.IsType<FolderUnit>(Assert.Single(ctx.Shelf.Units));
        Assert.Equal(FolderUnit.Normalize(first), unit.Path);
    }

    [Fact]
    public async Task Capture_NoExplorerWindow_ReportsClearly()
    {
        var ctx = new ShelfTestContext();

        var result = await ctx.Service.CaptureExplorerFolderAsync();

        Assert.False(result.Success);
        Assert.Empty(ctx.Shelf.Units);
        Assert.Contains("没有找到", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_VirtualLocation_ReportsClearly()
    {
        var ctx = new ShelfTestContext();
        ctx.ExplorerFolders.Locations.Add(new ExplorerWindowInfo("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "此电脑"));

        var result = await ctx.Service.CaptureExplorerFolderAsync();

        Assert.False(result.Success);
        Assert.Contains("不是本地文件夹", result.Message, StringComparison.Ordinal);
    }
}
