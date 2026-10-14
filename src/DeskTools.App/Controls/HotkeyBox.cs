using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskTools.Application.Settings;

namespace DeskTools.App.Controls;

/// <summary>
/// 热键录入框：聚焦后按下组合键即捕获，不需要手输文本。
/// </summary>
/// <remarks>
/// 交互细节（都来自「改热键必须零歧义」这一目标）：
/// <list type="bullet">
/// <item>只按修饰键不算完成，必须等到主键；</item>
/// <item><c>Esc</c> 放弃本次录入并还原显示；</item>
/// <item><c>Backspace</c> / <c>Delete</c> 清空（清空即无效，由 ViewModel 提示必须带修饰键）；</item>
/// <item>录入过程吃掉所有按键，避免触发窗口级快捷键。</item>
/// </list>
/// </remarks>
public sealed class HotkeyBox : Control
{
    /// <summary>当前手势。</summary>
    public static readonly DependencyProperty GestureProperty =
        DependencyProperty.Register(
            nameof(Gesture),
            typeof(HotkeyGesture),
            typeof(HotkeyBox),
            new FrameworkPropertyMetadata(
                HotkeyGesture.Default,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnGestureChanged));

    /// <summary>是否处于录入状态。</summary>
    public static readonly DependencyProperty IsCapturingProperty =
        DependencyProperty.Register(
            nameof(IsCapturing),
            typeof(bool),
            typeof(HotkeyBox),
            new FrameworkPropertyMetadata(false));

    /// <summary>捕获完成（含按键抬起）后触发，参数为新手势。</summary>
    public event EventHandler<HotkeyGesture>? GestureCaptured;

    /// <summary>创建录入框。</summary>
    public HotkeyBox()
    {
        Focusable = true;
        IsTabStop = true;
        Cursor = Cursors.Hand;
        GotKeyboardFocus += (_, _) => IsCapturing = true;
        LostKeyboardFocus += (_, _) => IsCapturing = false;
    }

    /// <summary>
    /// 点击即开始录入。
    /// 必须显式 Focus：WPF 的 <see cref="Control"/> 不会因为鼠标按下就自动取得键盘焦点，
    /// 不写这段就会出现「点了框、敲键盘没反应」。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        if (!IsKeyboardFocused)
        {
            Focus();
        }

        e.Handled = true;
    }

    /// <summary>当前手势（双向绑定）。</summary>
    public HotkeyGesture Gesture
    {
        get => (HotkeyGesture)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>是否正在等待按键。</summary>
    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        private set => SetValue(IsCapturingProperty, value);
    }

    /// <summary>开始录入（由点击触发）。</summary>
    public void BeginCapture() => Keyboard.Focus(this);

    /// <inheritdoc />
    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => new HotkeyBoxAutomationPeer(this);

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (!IsCapturing)
        {
            return;
        }

        // 录入期间不让按键外泄，否则会触发面板/窗口的快捷键
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape)
        {
            IsCapturing = false;
            Keyboard.ClearFocus();
            return;
        }

        // 纯修饰键：继续等待主键
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key is Key.Back or Key.Delete)
        {
            Commit(new HotkeyGesture { Key = string.Empty, Alt = false });
            return;
        }

        Commit(BuildGesture(key));
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is HotkeyBox box && e.NewValue is HotkeyGesture gesture)
        {
            box.GestureCaptured?.Invoke(box, gesture);
        }
    }

    private void Commit(HotkeyGesture gesture)
    {
        Gesture = gesture;
        IsCapturing = false;
        Keyboard.ClearFocus();
    }

    /// <summary>把 WPF 按键 + 当前修饰键状态转成设置用的手势模型。</summary>
    public static HotkeyGesture BuildGesture(Key key)
    {
        var modifiers = Keyboard.Modifiers;

        return new HotkeyGesture
        {
            Ctrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control,
            Alt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt,
            Shift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift,
            Win = (modifiers & ModifierKeys.Windows) == ModifierKeys.Windows,
            Key = ToKeyName(key),
        };
    }

    /// <summary>WPF <see cref="Key"/> → 与 <c>RegisterHotKey</c> 兼容的键名。</summary>
    public static string ToKeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => $"NUM{(int)(key - Key.NumPad0)}",
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Oem3 => "BACKQUOTE",
        Key.OemMinus => "MINUS",
        Key.OemPlus => "EQUALS",
        Key.OemOpenBrackets => "LEFTBRACKET",
        Key.OemCloseBrackets => "RIGHTBRACKET",
        Key.OemPipe => "BACKSLASH",
        Key.OemSemicolon => "SEMICOLON",
        Key.OemQuotes => "QUOTE",
        Key.OemComma => "COMMA",
        Key.OemPeriod => "PERIOD",
        Key.OemQuestion => "SLASH",
        Key.Space => "SPACE",
        Key.Tab => "TAB",
        Key.Enter => "ENTER",
        Key.Insert => "INSERT",
        Key.Home => "HOME",
        Key.End => "END",
        Key.PageUp => "PAGEUP",
        Key.PageDown => "PAGEDOWN",
        _ => key.ToString(),
    };
}
