using System.Windows;
using System.Windows.Controls;
using DeskShelf.App.ViewModels;

namespace DeskShelf.App.Templates;

/// <summary>
/// 把 <see cref="UnitViewModel"/> 分派到对应的 <see cref="DataTemplate"/>。
/// 模板键直接取自 <c>UnitCatalog</c> 的 <c>ViewKey</c> ——
/// 因此<strong>新增 unit 类型时这里无需改动一行代码</strong>，
/// 只要在资源字典里提供同名的 DataTemplate 即可。
/// </summary>
public sealed class UnitTemplateSelector : DataTemplateSelector
{
    /// <summary>找不到对应模板时使用的兜底模板键。</summary>
    public string FallbackKey { get; set; } = "View.Unit.Unknown";

    /// <inheritdoc />
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not UnitViewModel unit)
        {
            return base.SelectTemplate(item, container);
        }

        var viewKey = unit.Presentation.ViewKey;
        if (TryFind(viewKey, container) is { } template)
        {
            return template;
        }

        if (TryFind(FallbackKey, container) is { } fallback)
        {
            return fallback;
        }

        return base.SelectTemplate(item, container);
    }

    private DataTemplate? TryFind(string key, DependencyObject container)
    {
        if (container is FrameworkElement element)
        {
            if (element.TryFindResource(key) is DataTemplate fromElement)
            {
                return fromElement;
            }

            // 逐级向上：容器本身可能位于部分模板内部
            var parent = element.Parent;
            while (parent is FrameworkElement ancestor)
            {
                if (ancestor.TryFindResource(key) is DataTemplate fromAncestor)
                {
                    return fromAncestor;
                }

                parent = ancestor.Parent;
            }
        }

        return WpfApp.Current?.TryFindResource(key) as DataTemplate;
    }
}
