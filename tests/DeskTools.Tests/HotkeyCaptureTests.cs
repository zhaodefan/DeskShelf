using System.Windows.Input;
using DeskTools.App.Controls;
using DeskTools.Application.Settings;
using DeskTools.Infrastructure.Hotkeys;
using Xunit;

namespace DeskTools.Tests;

/// <summary>
/// 热键录入链路测试。
/// </summary>
/// <remarks>
/// 这条链路上有三处容易出错且互相耦合的映射：
/// WPF <see cref="Key"/> → 设置模型里的键名 → <c>RegisterHotKey</c> 的虚拟键码。
/// 任何一处不一致都会表现为「录入成功但热键不生效」，因此必须逐项钉住。
/// </remarks>
public sealed class HotkeyCaptureTests
{
    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.Z, "Z")]
    [InlineData(Key.D0, "0")]
    [InlineData(Key.D9, "9")]
    [InlineData(Key.F1, "F1")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.F24, "F24")]
    [InlineData(Key.Space, "SPACE")]
    [InlineData(Key.Oem3, "BACKQUOTE")]
    [InlineData(Key.OemMinus, "MINUS")]
    [InlineData(Key.OemQuestion, "SLASH")]
    public void ToKeyName_MapsWpfKeysToRegisterHotKeyNames(Key key, string expected)
        => Assert.Equal(expected, HotkeyBox.ToKeyName(key));

    [Theory]
    [InlineData("A", true)]
    [InlineData("0", true)]
    [InlineData("f12", true)]
    [InlineData("F24", true)]
    [InlineData("SPACE", true)]
    [InlineData("BACKQUOTE", true)]
    [InlineData("PAGEUP", true)]
    [InlineData("", false)]
    [InlineData("NOPE", false)]
    [InlineData("F25", false)]
    public void TryParseVirtualKey_AcceptsExactlyTheNamesWeProduce(string keyName, bool expected)
        => Assert.Equal(expected, GlobalHotkeyService.TryParseVirtualKey(keyName, out _));

    [Fact]
    public void CapturedGesture_SurvivesValidatorAndVirtualKeyParsing()
    {
        // 模拟「录入框产出 → 设置校验 → RegisterHotKey 解析」的完整链路
        var gesture = new HotkeyGesture { Ctrl = true, Alt = false, Shift = true, Key = HotkeyBox.ToKeyName(Key.Q) };

        Assert.True(gesture.IsValid);
        Assert.Equal(gesture, ShelfSettingsValidator.Normalize(ShelfSettings.Default with { Hotkey = gesture }).Hotkey);
        Assert.True(GlobalHotkeyService.TryParseVirtualKey(gesture.Key, out var virtualKey));
        Assert.Equal((uint)'Q', virtualKey);
    }

    [Fact]
    public void ModifierOnlyGesture_IsInvalid()
    {
        // 只按修饰键就结束录入的情况必须被拒绝，否则会出现「热键注册不上又看不出原因」
        var gesture = new HotkeyGesture { Ctrl = true, Alt = false, Key = string.Empty };
        Assert.False(gesture.IsValid);
    }

    [Fact]
    public void BackspaceClearedGesture_IsInvalidAndFallsBackToDefault()
    {
        var cleared = new HotkeyGesture { Alt = false, Key = string.Empty };

        Assert.False(cleared.IsValid);
        Assert.Equal(HotkeyGesture.Default, ShelfSettingsValidator.Normalize(ShelfSettings.Default with { Hotkey = cleared }).Hotkey);
    }

    [Fact]
    public void DisplayText_IsHumanReadable()
    {
        Assert.Equal("Alt+X", HotkeyGesture.Default.DisplayText);
        Assert.Equal("Ctrl+Shift+Q", new HotkeyGesture { Ctrl = true, Alt = false, Shift = true, Key = "Q" }.DisplayText);
        Assert.Equal("Ctrl+Alt+Win+F8", new HotkeyGesture { Ctrl = true, Alt = true, Win = true, Key = "F8" }.DisplayText);
    }
}
