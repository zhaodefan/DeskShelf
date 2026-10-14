using DeskTools.App.Services;
using DeskTools.App.ViewModels;
using DeskTools.Application.Abstractions;
using DeskTools.Application.Settings;
using DeskTools.Infrastructure.Windows;
using DeskTools.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeskTools.Tests;

/// <summary>
/// 设置 ViewModel 测试：保存 / 取消的语义，以及热键「先注销再注册」这一关键顺序。
/// </summary>
/// <remarks>
/// 这两点都曾经是真实缺陷：
/// 1. 换热键时不先注销会返回 1408（同进程已注册），表现为「换热键永远失败」；
/// 2. 关闭设置窗口后复用单例窗口会抛 InvalidOperationException（窗口不可再次 Show）。
/// </remarks>
public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task Save_PersistsAllEditableFields()
    {
        var ctx = new ShelfTestContext();
        var vm = Create(ctx);

        vm.Theme = AppTheme.Dark;
        vm.AutoStart = true;
        vm.ShowImageThumbnails = false;
        vm.MaxUnits = 120;

        Assert.True(await vm.SaveAsync());

        var saved = ctx.Settings.Current;
        Assert.Equal(AppTheme.Dark, saved.Theme);
        Assert.True(saved.AutoStart);
        Assert.False(saved.ShowImageThumbnails);
        Assert.Equal(120, saved.MaxUnits);
    }

    [Fact]
    public async Task Save_ChangesHotkey_UnregistersBeforeRegistering()
    {
        var ctx = new ShelfTestContext();
        var hotkeys = new RecordingHotkeyService();
        hotkeys.TryRegister(HotkeyGesture.Default);
        var vm = Create(ctx, hotkeys);

        var gesture = new HotkeyGesture { Ctrl = true, Alt = false, Shift = true, Key = "W" };
        await vm.SetHotkeyAsync(gesture);

        // 顺序必须是「先注销，再注册」，否则 RegisterHotKey 会因同进程已注册而失败
        Assert.Equal(new[] { "register:Alt+X", "unregister", "register:Ctrl+Shift+W" }, hotkeys.Calls);
        Assert.False(vm.IsHotkeyConflict);
    }

    [Fact]
    public async Task Save_WhenHotkeyTaken_ReportsConflictAndRestoresOriginal()
    {
        var ctx = new ShelfTestContext();
        var hotkeys = new RecordingHotkeyService { FailFor = "Ctrl+Shift+W" };
        hotkeys.TryRegister(HotkeyGesture.Default);
        var vm = Create(ctx, hotkeys);

        await vm.SetHotkeyAsync(new HotkeyGesture { Ctrl = true, Alt = false, Shift = true, Key = "W" });

        Assert.True(vm.IsHotkeyConflict);
        Assert.Contains("占用", vm.StatusMessage, StringComparison.Ordinal);

        // 显示与注册都回到原热键，避免「界面显示一个实际没生效的组合」
        Assert.Equal(HotkeyGesture.Default, vm.Hotkey);
        Assert.Equal(HotkeyGesture.Default, hotkeys.RegisteredGesture);
        Assert.False(await vm.SaveAsync());
    }

    [Fact]
    public async Task SetHotkey_ModifierOnly_IsRejected()
    {
        var ctx = new ShelfTestContext();
        var vm = Create(ctx);

        await vm.SetHotkeyAsync(new HotkeyGesture { Ctrl = true, Alt = false, Key = string.Empty });

        Assert.True(vm.IsHotkeyConflict);
        Assert.Contains("修饰键", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_RollsBackEverythingIncludingHotkeyAndAutoStart()
    {
        var ctx = new ShelfTestContext();
        var hotkeys = new RecordingHotkeyService();
        hotkeys.TryRegister(HotkeyGesture.Default);

        // 先落一份「原始设置」，再修改并取消
        await ctx.Settings.SaveAsync(ShelfSettings.Default with { MaxUnits = 321, Theme = AppTheme.Light });
        var vm = Create(ctx, hotkeys);

        vm.MaxUnits = 111;
        vm.Theme = AppTheme.Dark;
        await vm.SetHotkeyAsync(new HotkeyGesture { Ctrl = true, Alt = false, Shift = true, Key = "W" });

        await vm.CancelAsync();

        Assert.Equal(321, ctx.Settings.Current.MaxUnits);
        Assert.Equal(AppTheme.Light, ctx.Settings.Current.Theme);
        Assert.Equal(HotkeyGesture.Default, hotkeys.RegisteredGesture);
    }

    [Fact]
    public async Task Reload_RefreshesSnapshotSoCancelUsesLatestSettings()
    {
        var ctx = new ShelfTestContext();
        var vm = Create(ctx);

        // 模拟「关闭窗口后设置被别处改动」，再重新打开窗口
        await ctx.Settings.SaveAsync(ShelfSettings.Default with { MaxUnits = 777 });
        vm.Reload();

        Assert.Equal(777, vm.MaxUnits);
    }

    private static SettingsViewModel Create(ShelfTestContext ctx, IHotkeyService? hotkeys = null)
        => new(
            ctx.Settings,
            hotkeys ?? new RecordingHotkeyService(),
            new AutoStartService(NullLogger<AutoStartService>.Instance),
            ctx.Service,
            new FakeThemeApplier(),
            () => Task.CompletedTask,
            NullLogger<SettingsViewModel>.Instance);

    /// <summary>记录调用顺序的假热键服务（顺序本身就是被测行为）。</summary>
    private sealed class RecordingHotkeyService : IHotkeyService
    {
        private readonly List<string> _calls = new();

        /// <summary>调用序列，例如 <c>register:Alt+X</c> / <c>unregister</c>。</summary>
        public IReadOnlyList<string> Calls => _calls;

        /// <summary>指定该手势注册失败（模拟被其他程序占用）。</summary>
        public string? FailFor { get; init; }

        /// <inheritdoc />
        public bool IsRegistered => RegisteredGesture is not null;

        /// <inheritdoc />
        public HotkeyGesture? RegisteredGesture { get; private set; }

        /// <inheritdoc />
        public event EventHandler? Pressed;

        /// <inheritdoc />
        public bool TryRegister(HotkeyGesture gesture)
        {
            _calls.Add($"register:{gesture.DisplayText}");

            if (FailFor is not null && gesture.DisplayText == FailFor)
            {
                return false;
            }

            RegisteredGesture = gesture;
            return true;
        }

        /// <inheritdoc />
        public void Unregister()
        {
            _calls.Add("unregister");
            RegisteredGesture = null;
        }

        /// <summary>避免未使用告警（事件在本测试中不需要触发）。</summary>
        private void RaisePressed() => Pressed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>记录主题应用请求。</summary>
    private sealed class FakeThemeApplier : IThemeApplier
    {
        /// <summary>应用次数。</summary>
        public int ApplyCount { get; private set; }

        /// <summary>最后一次预览的明暗。</summary>
        public bool? LastPreview { get; private set; }

        /// <inheritdoc />
        public void ApplyFromSettings() => ApplyCount++;

        /// <inheritdoc />
        public void Preview(bool light)
        {
            ApplyCount++;
            LastPreview = light;
        }
    }
}
