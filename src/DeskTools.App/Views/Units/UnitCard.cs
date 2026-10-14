using System.Windows;
using System.Windows.Controls;
using DeskTools.App.ViewModels;

namespace DeskTools.App.Views.Units;

/// <summary>
/// unit 卡片外壳。三类 unit 共用同一外壳（边框、选中态、悬停小标），
/// 具体内容由 <see cref="ContentControl.ContentTemplate"/> 注入，
/// 模板由 <c>UnitTemplateSelector</c> 依据 unit 类型选择。
/// </summary>
/// <remarks>
/// 之所以做成控件而不是直接在 DataTemplate 里写 Border：
/// 卡片的状态（选中 / 编辑）需要在模板中以 Trigger 形式参与，而 Trigger 只能作用于
/// 控件自身的依赖属性；把状态提升为依赖属性后，卡片外壳就只需写一份。
/// </remarks>
public sealed class UnitCard : ContentControl
{
    /// <summary>标识 <see cref="IsSelected"/> 依赖属性。</summary>
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected),
            typeof(bool),
            typeof(UnitCard),
            new FrameworkPropertyMetadata(false));

    /// <summary>标识 <see cref="IsEditing"/> 依赖属性。</summary>
    public static readonly DependencyProperty IsEditingProperty =
        DependencyProperty.Register(
            nameof(IsEditing),
            typeof(bool),
            typeof(UnitCard),
            new FrameworkPropertyMetadata(false));

    /// <summary>是否被选中（由面板的选择状态驱动）。</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>是否处于行内编辑态。</summary>
    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }
}
