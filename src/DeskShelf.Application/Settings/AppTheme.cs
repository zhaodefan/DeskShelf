namespace DeskShelf.Application.Settings;

/// <summary>界面主题偏好。默认跟随系统（Windows 10 1809+ 可读注册表取值）。</summary>
public enum AppTheme
{
    /// <summary>跟随系统浅色/深色设置。</summary>
    System = 0,

    /// <summary>强制浅色。</summary>
    Light = 1,

    /// <summary>强制深色。</summary>
    Dark = 2,
}
