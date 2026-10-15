namespace DeskShelf.Infrastructure.Storage;

/// <summary>
/// 应用数据目录布局（%APPDATA%\DeskShelf）。
/// 全部路径集中于此，避免散落的 <c>Path.Combine</c> 与魔法字符串。
/// </summary>
public sealed class AppPaths
{
    /// <summary>默认数据根目录：<c>%APPDATA%\DeskShelf</c>。</summary>
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeskShelf");

    /// <summary>项目更名前的数据目录（<c>%APPDATA%\DeskTools</c>），用于一次性迁移。</summary>
    public static string LegacyRoot { get; } = Path.Combine(
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

    /// <summary>
    /// 从旧版数据目录迁移（<c>%APPDATA%\DeskTools</c> → <c>%APPDATA%\DeskShelf</c>）。
    /// </summary>
    /// <returns>是否发生了迁移。</returns>
    /// <remarks>
    /// 安全约束：只在「旧目录存在且新目录不存在」时执行；
    /// 优先整体移动（同盘瞬间完成），失败则递归复制后删除旧目录，再失败就保留旧目录并放弃迁移
    /// —— 绝不允许因为迁移失败而丢掉用户数据。
    /// </remarks>
    public bool TryMigrateFromLegacy()
    {
        try
        {
            if (!string.Equals(Root, DefaultRoot, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(LegacyRoot) ||
                Directory.Exists(Root))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(Root);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try
            {
                Directory.Move(LegacyRoot, Root);
                return true;
            }
            catch (Exception)
            {
                // 跨卷移动或目录被占用：退化为复制 + 删除
                CopyDirectory(LegacyRoot, Root);
                Directory.Delete(LegacyRoot, recursive: true);
                return true;
            }
        }
        catch (Exception)
        {
            // 迁移失败不影响启动：新目录稍后由 EnsureCreated 创建，用户会看到空面板
            return false;
        }
    }

    /// <summary>创建全部目录（幂等）。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
