using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DeskTools.Infrastructure.Windows;

/// <summary>
/// 开机自启（<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>）。
/// 写 HKCU 不需要管理员权限，因此不会弹 UAC —— 这是选择注册表而非计划任务的原因。
/// </summary>
public sealed class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskTools";

    private readonly ILogger<AutoStartService> _logger;

    /// <summary>创建自启服务。</summary>
    public AutoStartService(ILogger<AutoStartService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>当前是否已启用自启。</summary>
    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "读取自启注册表项失败。");
            return false;
        }
    }

    /// <summary>启用/禁用自启。</summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="executablePath">可执行文件路径；为 <c>null</c> 时使用当前进程路径。</param>
    /// <returns>操作是否成功。</returns>
    public bool SetEnabled(bool enabled, string? executablePath = null)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                _logger.LogWarning("无法打开自启注册表项。");
                return false;
            }

            if (enabled)
            {
                var path = executablePath ?? Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    _logger.LogWarning("无法确定可执行文件路径，取消自启设置。");
                    return false;
                }

                key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
                _logger.LogInformation("已启用开机自启：{Path}", path);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                _logger.LogInformation("已关闭开机自启。");
            }

            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogError(ex, "设置开机自启失败。");
            return false;
        }
    }
}
