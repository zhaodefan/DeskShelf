using System.ComponentModel;
using DeskShelf.App.ViewModels;
using DeskShelf.Application.Services;
using DeskShelf.Domain.Units;
using DeskShelf.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// UnitViewModel 的属性变更通知。
/// </summary>
/// <remarks>
/// 这里守护一个真实缺陷：卡片上的文本预览绑定到 <c>Text</c>，而行内编辑提交后
/// <c>PanelViewModel.SyncUnits</c> 只会复用已有 ViewModel（不重建），
/// 于是 <c>Text</c> 若不发通知，界面就永远显示旧值 ——
/// 曾表现为「新建文本输入完成后预览是空白，必须双击才看得到」。
/// </remarks>
public sealed class UnitViewModelTests
{
    [Fact]
    public void CommitText_RaisesPropertyChangedForText()
    {
        var ctx = new ShelfTestContext();
        var result = ctx.Service.CreateTextUnit(out var unitId);
        Assert.True(result.Success);

        var model = ctx.Shelf.Find(unitId!.Value);
        Assert.NotNull(model);

        var vm = new UnitViewModel(model!, ctx.Service, new ImageBytesCache(new FakeImageBytesSource(), NullLogger<ImageBytesCache>.Instance));
        var raised = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(vm.CommitText("新建后输入的内容"));

        Assert.Contains(nameof(UnitViewModel.Text), raised);
        Assert.Equal("新建后输入的内容", vm.Text);
    }

    [Fact]
    public void CommitText_WhenContentUnchanged_StillSyncsDisplayValue()
    {
        var ctx = new ShelfTestContext();
        ctx.Service.CreateTextUnit(out var unitId);

        var model = ctx.Shelf.Find(unitId!.Value)!;
        var vm = new UnitViewModel(model, ctx.Service, new ImageBytesCache(new FakeImageBytesSource(), NullLogger<ImageBytesCache>.Instance));

        // 第一次提交写入内容
        vm.CommitText("内容");

        // 第二次提交相同内容：领域层判定「无变化」返回 false，但显示值必须与落盘一致
        Assert.False(vm.CommitText("内容"));
        Assert.Equal("内容", vm.Text);
    }

    [Fact]
    public void CommitText_ReflectsNormalizedText()
    {
        // TextUnit.Normalize 会裁剪首尾空白：卡片预览应显示归一化后的内容，
        // 否则用户看到的与实际保存的不一致。
        var ctx = new ShelfTestContext();
        ctx.Service.CreateTextUnit(out var unitId);
        var model = ctx.Shelf.Find(unitId!.Value)!;
        var vm = new UnitViewModel(model, ctx.Service, new ImageBytesCache(new FakeImageBytesSource(), NullLogger<ImageBytesCache>.Instance));

        vm.CommitText("  有前后空格的内容  ");

        Assert.Equal("有前后空格的内容", vm.Text);
        Assert.Equal("有前后空格的内容", ((TextUnit)model).Text);
    }
}
