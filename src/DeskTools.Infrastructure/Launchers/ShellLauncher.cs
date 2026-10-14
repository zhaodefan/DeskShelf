using System.Diagnostics;
using DeskTools.Application.Abstractions;
using DeskTools.Domain.Units;
using DeskTools.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Launchers;

/// <summary>
/// 系统启动器：用默认程序打开图片、用资源管理器打开文件夹 / 定位文件。
/// 全部通过 ShellExecute 完成，不依赖具体的外部程序。
/// </summary>
public sealed class ShellLauncher : ISystemLauncher
{
    private readonly IImageStore _imageStore;
    private readonly AppPaths _paths;
    private readonly ILogger<ShellLauncher> _logger;

    /// <summary>创建启动器。</summary>
    public ShellLauncher(IImageStore imageStore, AppPaths paths, ILogger<ShellLauncher> logger)
    {
        _imageStore = imageStore ?? throw new ArgumentNullException(nameof(imageStore));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<bool> OpenAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        switch (unit.Capabilities.OpenBehavior)
        {
            case UnitOpenBehavior.OpenFolder when unit is FolderUnit folder:
                return Task.FromResult(OpenFolder(folder.Path));

            case UnitOpenBehavior.OpenImage when unit is ImageUnit image:
                return Task.FromResult(TryShellExecute(ResolveImagePath(image)));

            default:
                _logger.LogDebug("{Kind} unit 没有默认打开动作。", unit.Kind);
                return Task.FromResult(false);
        }
    }

    /// <inheritdoc />
    public Task<bool> RevealInExplorerAsync(ShelfUnit unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        return unit switch
        {
            FolderUnit folder => Task.FromResult(RevealPath(folder.Path)),
            ImageUnit image => Task.FromResult(RevealPath(ResolveImagePath(image))),
            _ => Task.FromResult(false),
        };
    }

    /// <inheritdoc />
    public Task<bool> OpenPathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        if (Directory.Exists(path))
        {
            return Task.FromResult(OpenFolder(path));
        }

        if (File.Exists(path))
        {
            return Task.FromResult(TryShellExecute(path));
        }

        _logger.LogWarning("路径不存在，无法打开：{Path}", path);
        return Task.FromResult(false);
    }

    /// <summary>把图片 unit 的存储键解析为本地绝对路径（缺失时返回原键值，由 ShellExecute 报告失败）。</summary>
    private string ResolveImagePath(ImageUnit image)
    {
        var fileName = System.IO.Path.GetFileName(image.ImageKey.Value);
        var full = System.IO.Path.Combine(_paths.Images, fileName);
        if (File.Exists(full))
        {
            return full;
        }

        _logger.LogWarning("图片文件缺失：{Path}", full);
        return full;
    }

    private bool OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            _logger.LogWarning("文件夹不存在：{Path}", path);
            return false;
        }

        return TryShellExecute("explorer.exe", $"\"{path}\"");
    }

    private bool RevealPath(string path)
    {
        if (File.Exists(path))
        {
            // /select, 会打开父目录并选中该文件
            return TryShellExecute("explorer.exe", $"/select,\"{path}\"");
        }

        return Directory.Exists(path) && OpenFolder(path);
    }

    private bool TryShellExecute(string fileName, string? arguments = null)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = true,
            };

            if (!string.IsNullOrEmpty(arguments))
            {
                startInfo.Arguments = arguments;
            }

            using var process = Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动外部程序失败：{File} {Args}", fileName, arguments);
            return false;
        }
    }
}
