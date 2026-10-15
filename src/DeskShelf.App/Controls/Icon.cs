using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeskShelf.App.Controls;

/// <summary>
/// 单色矢量图标控件（外观定义在 Themes/Icons.xaml 中）。
/// 所有图标以 16×16 视口绘制，默认描边风格。
/// 存在的意义：界面中写图标只需 <c>&lt;c:Icon Geometry="{StaticResource Icon.Copy}" /&gt;</c>，
/// 不必重复 <c>Path</c> 的描边、尺寸与命中测试设置。
/// </summary>
public sealed class Icon : Control
{
    /// <summary>图标几何数据。</summary>
    public static readonly DependencyProperty GeometryProperty =
        DependencyProperty.Register(
            nameof(Geometry),
            typeof(Geometry),
            typeof(Icon),
            new FrameworkPropertyMetadata(null));

    /// <summary>图标边长（正方形）。</summary>
    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(
            nameof(Size),
            typeof(double),
            typeof(Icon),
            new FrameworkPropertyMetadata(
                16d,
                FrameworkPropertyMetadataOptions.AffectsMeasure,
                OnSizeChanged));

    /// <summary>创建图标。</summary>
    public Icon()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Width = Size;
        Height = Size;
    }

    /// <summary>图标几何数据。</summary>
    public Geometry? Geometry
    {
        get => (Geometry?)GetValue(GeometryProperty);
        set => SetValue(GeometryProperty, value);
    }

    /// <summary>图标边长。</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Icon icon && e.NewValue is double size)
        {
            icon.Width = size;
            icon.Height = size;
        }
    }
}
