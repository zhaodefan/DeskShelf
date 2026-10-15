using System.Windows.Media;
using Microsoft.Win32;

namespace DeskShelf.Infrastructure.Theming;

/// <summary>系统主题检测（Windows 10 1809+ 的「应用模式」）。</summary>
public static class SystemThemeDetector
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ValueName = "AppsUseLightTheme";

    /// <summary>读取系统当前是否为浅色模式；读取失败时按浅色处理。</summary>
    public static bool IsLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            var raw = key?.GetValue(ValueName);

            // 该值在正常系统上为 DWORD，但受组策略或第三方工具影响也可能是字符串
            var value = raw switch
            {
                int intValue => intValue,
                string text when int.TryParse(text, out var parsed) => parsed,
                _ => (int?)null,
            };

            // 键或值缺失（例如从未打开过个性化设置）按浅色处理
            return value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>读取系统主题色（强调色），失败时回退到内置扁平蓝。</summary>
    public static Color GetAccentColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM", writable: false);
            if (key?.GetValue("AccentColor") is int raw)
            {
                // 注册表内为 ABGR 顺序
                var value = unchecked((uint)raw);
                var r = (byte)(value & 0xFF);
                var g = (byte)((value >> 8) & 0xFF);
                var b = (byte)((value >> 16) & 0xFF);
                return Color.FromRgb(r, g, b);
            }
        }
        catch (Exception)
        {
            // 忽略，使用默认强调色
        }

        return Color.FromRgb(0x3B, 0x82, 0xF6);
    }
}
