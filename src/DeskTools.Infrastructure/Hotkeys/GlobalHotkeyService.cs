using System.Runtime.InteropServices;
using System.Windows.Interop;
using DeskTools.Application.Abstractions;
using DeskTools.Application.Settings;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Hotkeys;

/// <summary>
/// 全局热键（默认 Alt+X）。基于 Win32 <c>RegisterHotKey</c> + 一个隐藏的
/// <see cref="HwndSource"/> 消息窗口接收 <c>WM_HOTKEY</c>。
/// </summary>
/// <remarks>
/// 失败语义很重要：热键可能被微信/QQ/输入法占用，此时 <b>不能</b> 抛异常导致启动失败，
/// 而是返回 <c>false</c> 交由上层提示用户改键。
/// </remarks>
public sealed class GlobalHotkeyService : IHotkeyService, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xD57F;

    // 修饰键（注意 MOD_SHIFT=0x0004 与 MOD_NOREPEAT 冲突，故统一使用 0x4000 抑制长按重复触发）
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly ILogger<GlobalHotkeyService> _logger;
    private HwndSource? _source;
    private HotkeyGesture? _registered;

    /// <summary>创建热键服务。消息窗口在首次注册时惰性创建。</summary>
    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRegistered => _registered is not null;

    /// <inheritdoc />
    public HotkeyGesture? RegisteredGesture => _registered;

    /// <inheritdoc />
    public event EventHandler? Pressed;

    /// <inheritdoc />
    public bool TryRegister(HotkeyGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(gesture);

        if (!gesture.IsValid)
        {
            _logger.LogWarning("热键手势无效：{Gesture}", gesture.DisplayText);
            return false;
        }

        if (!TryParseVirtualKey(gesture.Key, out var virtualKey))
        {
            _logger.LogWarning("无法识别热键主键：{Key}", gesture.Key);
            return false;
        }

        EnsureMessageWindow();
        if (_source is null)
        {
            return false;
        }

        Unregister();

        var modifiers = BuildModifiers(gesture);
        if (!RegisterHotKey(_source.Handle, HotkeyId, modifiers, virtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("注册全局热键 {Gesture} 失败（Win32 错误 {Error}），通常是被其他程序占用。",
                gesture.DisplayText, error);
            return false;
        }

        _registered = gesture;
        _logger.LogInformation("全局热键已注册：{Gesture}", gesture.DisplayText);
        return true;
    }

    /// <inheritdoc />
    public void Unregister()
    {
        if (_source is not null && _registered is not null)
        {
            UnregisterHotKey(_source.Handle, HotkeyId);
        }

        _registered = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Unregister();
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }

    private void EnsureMessageWindow()
    {
        if (_source is not null)
        {
            return;
        }

        // 使用 HWND_MESSAGE 父窗口创建消息专用窗口：不出现在任务栏、不抢焦点
        var parameters = new HwndSourceParameters("DeskTools.HotkeySink")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        _logger.LogDebug("热键消息窗口已创建，句柄 {Handle}。", _source.Handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            _logger.LogDebug("全局热键触发。");
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    private static uint BuildModifiers(HotkeyGesture gesture)
    {
        var modifiers = ModNoRepeat;
        if (gesture.Ctrl)
        {
            modifiers |= ModControl;
        }

        if (gesture.Alt)
        {
            modifiers |= ModAlt;
        }

        if (gesture.Shift)
        {
            modifiers |= ModShift;
        }

        if (gesture.Win)
        {
            modifiers |= ModWin;
        }

        return modifiers;
    }

    /// <summary>把设置中的主键名解析为 Win32 虚拟键码。</summary>
    public static bool TryParseVirtualKey(string key, out uint virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var token = key.Trim().ToUpperInvariant();

        // 单字符：A-Z 的虚拟键码与 ASCII 同值；0-9 同理
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            virtualKey = token[0];
            return true;
        }

        // 功能键 F1..F24
        if (token.Length is >= 2 and <= 3 && token[0] == 'F' && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + (fn - 1));
            return true;
        }

        var named = token switch
        {
            "SPACE" => 0x20u,
            "TAB" => 0x09u,
            "ENTER" or "RETURN" => 0x0Du,
            "ESC" or "ESCAPE" => 0x1Bu,
            "BACKQUOTE" or "OEM3" => 0xC0u,
            "MINUS" or "OEM_MINUS" => 0xBDu,
            "EQUALS" or "OEM_PLUS" => 0xBBu,
            "LEFTBRACKET" or "OEM_4" => 0xDBu,
            "RIGHTBRACKET" or "OEM_6" => 0xDDu,
            "BACKSLASH" or "OEM_5" => 0xDCu,
            "SEMICOLON" or "OEM_1" => 0xBAu,
            "QUOTE" or "OEM_7" => 0xDEu,
            "COMMA" or "OEM_COMMA" => 0xBCu,
            "PERIOD" or "OEM_PERIOD" => 0xBEu,
            "SLASH" or "OEM_2" => 0xBFu,
            "INSERT" => 0x2Du,
            "DELETE" => 0x2Eu,
            "HOME" => 0x24u,
            "END" => 0x23u,
            "PAGEUP" or "PGUP" => 0x21u,
            "PAGEDOWN" or "PGDN" => 0x22u,
            _ => 0u,
        };

        virtualKey = named;
        return named != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
