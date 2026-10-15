using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Domain.Units;

/// <summary>
/// 文件夹 unit（路径 unit）：面板内展示「文件夹图标 + 路径」，双击用资源管理器打开。
/// 按需求，它<b>不参与</b>剪贴板复制，也无法通过粘贴产生 —— 只能由资源管理器拖入。
/// </summary>
public sealed class FolderUnit : ShelfUnit
{
    /// <summary>新建文件夹 unit。</summary>
    public FolderUnit(string path, UnitId id, UnitOrigin origin, DateTimeOffset createdAt)
        : base(id, origin, createdAt)
    {
        Path = Normalize(path);
    }

    /// <inheritdoc />
    public override UnitKind Kind => UnitKind.Folder;

    /// <inheritdoc />
    public override UnitCapabilities Capabilities { get; } =
        new(CanCopyToClipboard: false, CanEditText: false, OpenBehavior: UnitOpenBehavior.OpenFolder);

    /// <summary>规范化后的绝对路径。</summary>
    public string Path { get; }

    /// <summary>末级目录名，用于卡片主标题。</summary>
    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    /// <inheritdoc />
    public override string ContentFingerprint => Path;

    /// <inheritdoc />
    public override string ContentSummary => Path;

    /// <summary>规范化路径：去首尾空白、去末尾分隔符、尽量转绝对路径。</summary>
    public static string Normalize(string? path)
    {
        var value = (path ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            value = System.IO.Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            // 非法路径保留原样，由 UI 以「路径不可用」状态呈现
            return value;
        }

        var root = System.IO.Path.GetPathRoot(value);
        // 保留盘符根（C:\）末尾的分隔符，其余去掉
        if (!string.IsNullOrEmpty(root) &&
            !string.Equals(value, root, StringComparison.OrdinalIgnoreCase) &&
            value.Length > root.Length)
        {
            value = value.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        return value;
    }
}
