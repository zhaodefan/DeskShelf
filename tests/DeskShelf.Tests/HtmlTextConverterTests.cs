using DeskShelf.Application.Services;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>HTML→纯文本降级：浏览器只给 HTML Format 时的最后一道保障。</summary>
public sealed class HtmlTextConverterTests
{
    [Fact]
    public void ToPlainText_StripsTagsAndDecodesEntities()
    {
        var html = "<div>Hello&nbsp;<b>World</b> &amp; more</div>";

        var text = HtmlTextConverter.ToPlainText(html);

        Assert.Equal("Hello World & more", text);
    }

    [Fact]
    public void ToPlainText_ConvertsBlockTagsToNewlines()
    {
        var html = "<p>第一段</p><p>第二段</p>";

        var text = HtmlTextConverter.ToPlainText(html);

        Assert.Equal("第一段\n第二段", text);
    }

    [Fact]
    public void ToPlainText_UsesCfHtmlFragmentMarkers()
    {
        var cfHtml = """
            Version:1.0
            StartHTML:0000000105
            EndHTML:0000000199
            StartFragment:0000000141
            EndFragment:0000000193
            <html><body><!--StartFragment--><span>正文内容</span><!--EndFragment--></body></html>
            """;

        var text = HtmlTextConverter.ToPlainText(cfHtml);

        Assert.Equal("正文内容", text);
    }

    [Fact]
    public void ToPlainText_DropsScriptAndStyle()
    {
        var html = "<style>.a{color:red}</style><script>alert('x')</script><div>可见文字</div>";

        var text = HtmlTextConverter.ToPlainText(html);

        Assert.Equal("可见文字", text);
    }

    [Fact]
    public void ToPlainText_ListItemsGetBullets()
    {
        var html = "<ul><li>甲</li><li>乙</li></ul>";

        var text = HtmlTextConverter.ToPlainText(html);

        Assert.Contains("• 甲", text, StringComparison.Ordinal);
        Assert.Contains("• 乙", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToPlainText_EmptyInputReturnsEmpty(string? input)
    {
        Assert.Equal(string.Empty, HtmlTextConverter.ToPlainText(input));
    }
}
