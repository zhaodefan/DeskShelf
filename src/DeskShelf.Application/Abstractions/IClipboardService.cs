using DeskShelf.Domain;

namespace DeskShelf.Application.Abstractions;

/// <summary>
/// 从剪贴板读取外部内容。实现位于基础设施层；用例层只依赖本接口，
/// 因此 UI 与业务代码中不允许出现 <c>System.Windows.Clipboard</c>。
/// </summary>
public interface IClipboardService
{
    /// <summary>读取当前剪贴板内容；无可用内容时返回 <c>null</c>。</summary>
    /// <remarks>纯文本、位图、文件列表三种形态会被归一化为 <see cref="RawContent"/>。</remarks>
    Task<RawContent?> TryReadAsync(CancellationToken cancellationToken = default);

    /// <summary>把内容写入剪贴板。</summary>
    Task WriteAsync(RawContent content, CancellationToken cancellationToken = default);
}
