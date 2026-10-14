using System.Runtime.InteropServices;
using DeskTools.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Windows;

/// <summary>
/// 通过 <c>Shell.Application</c> COM 枚举已打开的资源管理器窗口位置。
/// </summary>
/// <remarks>
/// 选它而不是枚举窗口标题的原因：标题可能被后缀污染（例如「ai-agent-book - 文件资源管理器」），
/// 而 Shell 的 <c>Folder.Self.Path</c> 直接给出真实路径，且天然支持「这台电脑」「控制面板」等虚拟位置。
/// 窗口顺序由 Shell 维护，最近激活的窗口排在最前。
/// </remarks>
public sealed class ShellExplorerFolderSource : IExplorerFolderSource
{
    private readonly ILogger<ShellExplorerFolderSource> _logger;

    /// <summary>创建查询器。</summary>
    public ShellExplorerFolderSource(ILogger<ShellExplorerFolderSource> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public IReadOnlyList<ExplorerWindowInfo> GetOpenLocations()
    {
        var results = new List<ExplorerWindowInfo>();

        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                _logger.LogWarning("系统中找不到 Shell.Application，无法枚举资源管理器窗口。");
                return results;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic windows = shell.Windows();
                var count = (int)windows.Count;

                for (var i = 0; i < count; i++)
                {
                    dynamic? window = null;
                    try
                    {
                        window = windows.Item(i);
                        string location = window.LocationURL;
                        string displayName = window.LocationName;

                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            results.Add(new ExplorerWindowInfo(location, displayName ?? string.Empty));
                        }
                    }
                    catch (Exception ex)
                    {
                        // 个别窗口可能在枚举过程中被关闭
                        _logger.LogDebug(ex, "跳过无法读取的资源管理器窗口（索引 {Index}）。", i);
                    }
                    finally
                    {
                        if (window is not null && Marshal.IsComObject(window))
                        {
                            Marshal.ReleaseComObject(window);
                        }
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "枚举资源管理器窗口失败。");
        }

        _logger.LogDebug("发现 {Count} 个打开的资源管理器位置。", results.Count);
        return results;
    }
}
