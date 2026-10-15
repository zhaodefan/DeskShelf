using System.Globalization;

namespace DeskShelf.Domain.ValueObjects;

/// <summary>
/// 版本号（语义化版本的实用子集：<c>major.minor.patch</c>，可选 <c>-prerelease</c> 后缀）。
/// </summary>
/// <remarks>
/// 不引第三方版本库的原因：更新检查只需要「解析 + 比较」两件事，
/// 自己实现能保证与 GitHub Release 标签（<c>v1.2.3</c>）的解析规则完全可控且可测试。
/// </remarks>
public readonly record struct SemanticVersion : IComparable<SemanticVersion>
{
    /// <summary>创建版本号。</summary>
    /// <param name="major">主版本。</param>
    /// <param name="minor">次版本。</param>
    /// <param name="patch">修订号。</param>
    /// <param name="prerelease">预发布标识（例如 <c>rc.1</c>）；正式版为 <c>null</c>。</param>
    public SemanticVersion(int major, int minor, int patch, string? prerelease = null)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = string.IsNullOrWhiteSpace(prerelease) ? null : prerelease.Trim();
    }

    /// <summary>主版本号。</summary>
    public int Major { get; }

    /// <summary>次版本号。</summary>
    public int Minor { get; }

    /// <summary>修订号。</summary>
    public int Patch { get; }

    /// <summary>预发布标识；正式版为 <c>null</c>。</summary>
    public string? Prerelease { get; }

    /// <summary>是否为正式版（无预发布后缀）。</summary>
    public bool IsStable => Prerelease is null;

    /// <summary>
    /// 解析版本字符串。兼容 <c>1.2.3</c> / <c>v1.2.3</c> / <c>1.2.3.0</c> / <c>v1.2.3-rc.1</c> 等形式。
    /// </summary>
    /// <param name="text">待解析文本。</param>
    /// <param name="version">解析结果。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        // 去掉常见的 'v' / 'V' 前缀与构建元数据（+ 之后的部分不参与比较）
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
        {
            value = value[1..];
        }

        var plusIndex = value.IndexOf('+');
        if (plusIndex >= 0)
        {
            value = value[..plusIndex];
        }

        string? prerelease = null;
        var dashIndex = value.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = value[(dashIndex + 1)..];
            value = value[..dashIndex];
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 4)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            // 每位只取前导数字，容忍 "3beta" 这类写法
            var digits = new string(parts[i].TakeWhile(char.IsAsciiDigit).ToArray());
            if (digits.Length == 0 ||
                !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>
    /// 与另一个版本比较。规则遵循语义化版本：
    /// 依次比较 major / minor / patch；相等时<b>预发布版低于正式版</b>，两者都是预发布时按字符串比较。
    /// </summary>
    public int CompareTo(SemanticVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0)
        {
            return result;
        }

        result = Minor.CompareTo(other.Minor);
        if (result != 0)
        {
            return result;
        }

        result = Patch.CompareTo(other.Patch);
        if (result != 0)
        {
            return result;
        }

        return (Prerelease, other.Prerelease) switch
        {
            (null, null) => 0,
            (null, not null) => 1,      // 正式版 > 预发布版
            (not null, null) => -1,
            (not null, not null) => string.CompareOrdinal(Prerelease, other.Prerelease),
        };
    }

    /// <summary>是否比另一个版本更新。</summary>
    public bool IsNewerThan(SemanticVersion other) => CompareTo(other) > 0;

    /// <summary>运算符 &gt;。</summary>
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    /// <summary>运算符 &lt;。</summary>
    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    /// <summary>运算符 &gt;=。</summary>
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    /// <summary>运算符 &lt;=。</summary>
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    /// <inheritdoc />
    public override string ToString()
        => Prerelease is null
            ? $"{Major}.{Minor}.{Patch}"
            : $"{Major}.{Minor}.{Patch}-{Prerelease}";
}
