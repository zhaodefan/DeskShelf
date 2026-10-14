using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DeskTools.App.Behaviors;

/// <summary>
/// 让任意元素承担「拖动窗口」的职责（用于无边框窗口的自定义标题栏）。
/// </summary>
/// <remarks>
/// 使用 HwndSource 的 <c>WM_NCLBUTTONDOWN</c> 方式而非 <c>DragMove()</c>：
/// 前者由系统接管拖动循环，能正确处理高 DPI、多屏与「拖到屏幕边缘吸附」，
/// 而 <c>DragMove</c> 在快速拖动时容易抛 <see cref="InvalidOperationException"/>。
/// </remarks>
public static class WindowDragBehavior
{
    /// <summary>是否启用拖动行为的附加属性。</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(WindowDragBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    /// <summary>读取是否启用。</summary>
    public static bool GetIsEnabled(DependencyObject element)
        => element is not null && (bool)element.GetValue(IsEnabledProperty);

    /// <summary>设置是否启用。</summary>
    public static void SetIsEnabled(DependencyObject element, bool value)
        => element?.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.MouseLeftButtonDown -= OnMouseLeftButtonDown;
        if (e.NewValue is true)
        {
            element.MouseLeftButtonDown += OnMouseLeftButtonDown;
        }
    }

    private static void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject element || e.ClickCount != 1)
        {
            return;
        }

        // 从小标按钮上按下时不触发拖动
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        var window = Window.GetWindow(element);
        if (window is null)
        {
            return;
        }

        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        e.Handled = true;
        NativeMethods.SendMessage(handle, NativeMethods.WmNcLButtonDown, (IntPtr)NativeMethods.HtCaption, IntPtr.Zero);
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private static class NativeMethods
    {
        public const int WmNcLButtonDown = 0x00A1;
        public const int HtCaption = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }
}
