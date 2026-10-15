using System.Windows;
using System.Windows.Media;

namespace DeskShelf.App.Themes;

/// <summary>
/// 向标准控件附加图标的附加属性。
/// 存在的意义：WPF 的 <see cref="System.Windows.Controls.MenuItem"/> 要求图标是
/// <c>Icon</c> 属性上的 <see cref="UIElement"/>，直接写 <c>Geometry</c> 需要一长串
/// <c>Path</c> 元素；这里让 XAML 可以只写 <c>Icon="{StaticResource Icon.Copy}"</c>。
/// </summary>
public static class IconProperties
{
    /// <summary>图标几何数据附加属性。</summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon",
            typeof(Geometry),
            typeof(IconProperties),
            new PropertyMetadata(null));

    /// <summary>读取图标几何。</summary>
    public static Geometry? GetIcon(DependencyObject element)
        => element is null ? null : (Geometry?)element.GetValue(IconProperty);

    /// <summary>设置图标几何。</summary>
    public static void SetIcon(DependencyObject element, Geometry? value)
        => element?.SetValue(IconProperty, value);
}
