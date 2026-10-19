using DeskShelf.Domain.Units;
using DeskShelf.Domain.ValueObjects;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 文件 unit 的领域语义。
/// </summary>
/// <remarks>
/// 文件 unit 的核心价值是「双击打开」，因此能力的正确性（OpenBehavior / CanReveal）
/// 与路径规范化（决定去重与是否存在判定）是这组测试的重点。
/// </remarks>
public sealed class FileUnitTests
{
    private static FileUnit Create(string path)
        => new(path, UnitId.New(), UnitOrigin.DragDrop, DateTimeOffset.UtcNow);

    [Fact]
    public void KindAndCapabilities_AllowOpenAndCopyButNotEdit()
    {
        var unit = Create(@"C:\temp\report.pdf");

        Assert.Equal(UnitKind.File, unit.Kind);
        Assert.Equal(UnitOpenBehavior.OpenFile, unit.Capabilities.OpenBehavior);
        Assert.True(unit.Capabilities.CanCopyToClipboard);
        Assert.False(unit.Capabilities.CanEditText);
        Assert.True(unit.Capabilities.CanReveal);
        Assert.True(unit.Capabilities.HasAnyAction);
    }

    [Fact]
    public void DisplayNameAndExtension_AreDerivedFromPath()
    {
        var unit = Create(@"C:\temp\report.PDF");

        Assert.Equal("report.PDF", unit.DisplayName);
        Assert.Equal(".pdf", unit.Extension);   // 扩展名统一为小写，便于图标/分类判断
    }

    [Fact]
    public void Extension_HandlesFileWithoutExtension()
    {
        var unit = Create(@"C:\temp\LICENSE");

        Assert.Equal("LICENSE", unit.DisplayName);
        Assert.Equal(string.Empty, unit.Extension);
    }

    [Fact]
    public void Fingerprint_IsNormalizedPathSoDuplicatesCollapse()
    {
        // 同一个文件的大小写/末尾分隔符差异应视为同一指纹（去重依赖它）
        var lower = Create(@"C:\temp\notes.txt");
        var upper = Create(@"C:\TEMP\notes.txt");

        Assert.Equal(lower.ContentFingerprint, upper.ContentFingerprint, ignoreCase: true);
        Assert.Equal(lower.ContentSummary, lower.Path);
    }

    [Fact]
    public void Normalize_MakesAbsoluteAndTrimsTrailingSeparator()
    {
        var normalized = FileUnit.Normalize(@"C:\temp\folder\");

        Assert.Equal(@"C:\temp\folder", normalized);
    }

    [Fact]
    public void Normalize_KeepsDriveRootIntact()
    {
        // 盘符根不能被裁掉分隔符（否则 C:\ 变成 C:，语义完全不同）
        Assert.Equal(@"C:\", FileUnit.Normalize(@"C:\"));
    }

    [Fact]
    public void Normalize_HandlesEmptyAndInvalidInput()
    {
        Assert.Equal(string.Empty, FileUnit.Normalize(null));
        Assert.Equal(string.Empty, FileUnit.Normalize("   "));
        // 非法字符不应抛出：保留原样交给 UI 呈现为「路径不可用」
        Assert.False(string.IsNullOrWhiteSpace(FileUnit.Normalize("C:\\a\0b")));
    }
}
