using System.Windows.Automation.Peers;
using DeskTools.Application.Settings;

namespace DeskTools.App.Controls;

/// <summary>
/// <see cref="HotkeyBox"/> 的自动化节点。
/// </summary>
/// <remarks>
/// 自定义 <c>Control</c> 默认没有自动化节点，屏幕阅读器与 UI 自动化工具都看不到它。
/// 这里把它暴露为 Button 类型的控件，名称即当前手势（例如「Alt+X」），
/// 既满足无障碍要求，也让自动化验证能够定位到它。
/// </remarks>
public sealed class HotkeyBoxAutomationPeer : FrameworkElementAutomationPeer
{
    /// <summary>创建自动化节点。</summary>
    public HotkeyBoxAutomationPeer(HotkeyBox owner)
        : base(owner)
    {
    }

    /// <inheritdoc />
    protected override string GetClassNameCore() => nameof(HotkeyBox);

    /// <inheritdoc />
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

    /// <inheritdoc />
    protected override string GetNameCore()
    {
        if (Owner is not HotkeyBox box)
        {
            return base.GetNameCore();
        }

        var gesture = box.Gesture;
        return gesture is null || string.IsNullOrWhiteSpace(gesture.DisplayText)
            ? "快捷键输入框"
            : $"快捷键：{gesture.DisplayText}";
    }

    /// <inheritdoc />
    protected override string GetHelpTextCore() => "点击后按下组合键即可录入快捷键；Esc 放弃，Backspace 清空。";
}
