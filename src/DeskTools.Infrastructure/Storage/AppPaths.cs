namespace DeskTools.Infrastructure.Storage;

/// <summary>
/// 应用数据目录布局（%APPDATA%\DeskTools）。
/// 全部路径集中于此，避免散落的 <c>Path.Combine</c> 与魔法字符串。
/// </summary>
public sealed class AppPaths
{
    /// <summary>默认数据根目录：<c>%APPDATA%\DeskTools</c>。</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskTools");

    /// <summary>创建目录布局描述。</summary>
    /// <param name="root">数据根目录；为 <c>null</c> 时使用 <see cref="DefaultRoot"/>。</param>
    public AppPaths(string? root = null)
    {
        Root = string.IsNullOrWhiteSpace(root) ? DefaultRoot : root;
        Images = Path.Combine(Root, "images");
        Logs = Path.Combine(Root, "logs");
        Backups = Path.Combine(Root, "backup");
        ShelfFile = Path.Combine(Root, "shelf.json");
        SettingsFile = Path.Combine(Root, "settings.json");
    }

    /// <summary>数据根目录。</summary>
    public string Root { get; }

    /// <summary>图片目录（原图 + 缩略图）。</summary>
    public string Images { get; }

    /// <summary>日志目录。</summary>
    public string Logs { get; }

    /// <summary>损坏数据备份目录。</summary>
    public string Backups { get; }

    /// <summary>置物架数据文件。</summary>
    public string ShelfFile { get; }

    /// <summary>设置文件。</summary>
    public string SettingsFile { get; }

    /// <summary>创建全部目录（幂等）。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
    }
}
