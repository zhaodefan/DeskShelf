using DeskShelf.Domain.ValueObjects;

namespace DeskShelf.Domain.Units;

/// <summary>
/// 文件 unit：面板内展示「文件图标 + 文件名 + 完整路径」，双击用系统默认程序打开该文件。
/// </summary>
/// <remarks>
/// 与 <see cref="FolderUnit"/> 的区别：
/// <list type="bullet">
/// <item>打开动作是「用默认程序打开文件」而非「在资源管理器中打开目录」；</item>
/// <item>允许复制到剪贴板（以 <c>FileDrop</c> 形式，可粘贴到资源管理器）；</item>
/// <item>路径不受「去掉末尾分隔符」的规范化影响（文件路径本就不应带分隔符）。</item>
/// </list>
/// 只保存路径而不复制文件本体：用户在面板里看到的是「指向原文件的快捷方式」，
/// 原文件被移动/删除后该 unit 会显示为失效状态（由 UI 呈现），不做自动清理。
/// </remarks>
public sealed class FileUnit : ShelfUnit
{
    /// <summary>新建文件 unit。</summary>
    public FileUnit(string path, UnitId id, UnitOrigin origin, DateTimeOffset createdAt)
        : base(id, origin, createdAt)
    {
        Path = Normalize(path);
    }

    /// <inheritdoc />
    public override UnitKind Kind => UnitKind.File;

    /// <inheritdoc />
    public override UnitCapabilities Capabilities { get; } =
        new(CanCopyToClipboard: true, CanEditText: false, OpenBehavior: UnitOpenBehavior.OpenFile);

    /// <summary>规范化后的绝对路径。</summary>
    public string Path { get; }

    /// <summary>文件名（含扩展名），用于卡片主标题。</summary>
    public string DisplayName
    {
        get
        {
            var name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrEmpty(name) ? Path : name;
        }
    }

    /// <summary>扩展名（含点号，小写；无扩展名时为空字符串）。</summary>
    public string Extension => System.IO.Path.GetExtension(Path).ToLowerInvariant();

    /// <inheritdoc />
    public override string ContentFingerprint => Path;

    /// <inheritdoc />
    public override string ContentSummary => Path;

    /// <summary>规范化路径：去首尾空白、尽量转绝对路径、去掉误带的末尾分隔符。</summary>
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

        // 文件路径不应以分隔符结尾（拖拽时偶有尾部分隔符）
        var root = System.IO.Path.GetPathRoot(value);
        if (!string.IsNullOrEmpty(root) &&
            !string.Equals(value, root, StringComparison.OrdinalIgnoreCase) &&
            value.Length > root.Length)
        {
            value = value.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        }

        return value;
    }
}
