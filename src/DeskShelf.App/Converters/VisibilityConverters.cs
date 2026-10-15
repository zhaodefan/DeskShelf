using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DeskShelf.App.Converters;

/// <summary>
/// 值 → 可见性。用于「有图标才占位」这类场景，
/// 避免为每种可空值都写一个专用转换器。
/// </summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    /// <summary>值为 <c>null</c> 时是否仍然可见（默认折叠）。</summary>
    public bool VisibleWhenNull { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null;
        if (value is string text)
        {
            hasValue = !string.IsNullOrEmpty(text);
        }

        var visible = VisibleWhenNull ? !hasValue : hasValue;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("该转换器仅支持单向绑定。");
}

/// <summary>布尔 → 可见性（<c>true</c> 时可见）。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    /// <summary>为 <c>true</c> 时是否折叠（反相模式）。</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("该转换器仅支持单向绑定。");
}

/// <summary>布尔取反（用于「忙碌时禁用按钮」这类绑定）。</summary>
public sealed class BooleanInverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>值是否为 <c>null</c> → 布尔（用于「进度总量未知时显示不确定进度条」）。</summary>
public sealed class NullToBooleanConverter : IValueConverter
{
    /// <summary>值为 <c>null</c> 时返回的结果（默认 <c>true</c>）。</summary>
    public bool WhenNull { get; set; } = true;

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? WhenNull : !WhenNull;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("该转换器仅支持单向绑定。");
}
