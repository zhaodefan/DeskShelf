using System.Collections.Specialized;
using System.Windows;
using DeskShelf.Domain;
using DeskShelf.Domain.Units;
using DeskShelf.Infrastructure.Clipboard;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 拖放/剪贴板共用的 <see cref="DataObjectReader"/> 解析链路。
/// </summary>
/// <remarks>
/// 为什么需要这层：文件 unit 的入口是「从资源管理器拖入」，
/// 若只测工厂（<c>UnitContentFactory</c>）就跳过了 OLE 数据对象的读取环节 ——
/// 而「拖进来没反应」正是修复历史里最常见的一类问题。
/// 这里用真实的 <see cref="DataObject"/> 构造 <c>FileDrop</c> 负载，
/// 覆盖「拖拽 → 解析 → RawContent」这一段。
/// </remarks>
public sealed class DataObjectReaderTests
{
    /// <summary>在 STA 线程上执行（WPF 数据对象有线程亲和性要求）。</summary>
    private static T RunSta<T>(Func<T> action)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("STA 线程执行失败：" + failure.Message, failure);
        }

        return result;
    }

    [Fact]
    public void FileDrop_IsReadAsPathsAndBecomesPathContent()
    {
        var path = @"C:\Users\Public\report.pdf";

        var (paths, content) = RunSta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { path });

            var payload = DataObjectReader.TryReadPayload(data);
            var raw = DataObjectReader.ToRawContent(payload, UnitOrigin.DragDrop);
            return (payload?.FilePaths, raw);
        });

        Assert.NotNull(paths);
        Assert.Equal(path, Assert.Single(paths!));

        Assert.NotNull(content);
        Assert.Equal(RawContentKind.Paths, content!.Kind);
        Assert.Equal(path, Assert.Single(content.Paths));
        Assert.Equal(UnitOrigin.DragDrop, content.Origin);
    }

    [Fact]
    public void FileDrop_FromStringCollection_IsAlsoAccepted()
    {
        // 某些拖放源（含部分第三方资源管理器）以 StringCollection 形式提供 FileDrop
        var paths = new[] { @"C:\a.txt", @"C:\b.txt" };

        var read = RunSta(() =>
        {
            var data = new DataObject();
            var collection = new StringCollection();
            collection.AddRange(paths);
            data.SetData(DataFormats.FileDrop, collection);
            return DataObjectReader.TryReadFilePaths(data);
        });

        Assert.NotNull(read);
        Assert.Equal(paths, read!);
    }

    [Fact]
    public void EmptyFileDrop_YieldsNoContent()
    {
        var content = RunSta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, Array.Empty<string>());
            return DataObjectReader.ToRawContent(DataObjectReader.TryReadPayload(data), UnitOrigin.DragDrop);
        });

        Assert.Null(content);
    }

    [Fact]
    public void PlainText_StillTakesEffectWhenNoFilesDropped()
    {
        // 回归保护：新增文件分支不应影响原有的文本拖入
        var content = RunSta(() =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, "拖入的文本");
            return DataObjectReader.ToRawContent(DataObjectReader.TryReadPayload(data), UnitOrigin.DragDrop);
        });

        Assert.NotNull(content);
        Assert.Equal(RawContentKind.Text, content!.Kind);
        Assert.Equal("拖入的文本", content.Text);
    }
}
