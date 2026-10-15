using DeskShelf.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace DeskShelf.App.Services;

/// <summary>
/// 缩略图加载调度器：串行、限速地逐个加载可见卡片的缩略图。
/// </summary>
/// <remarks>
/// 为什么需要它：图片解码必须异步（不能阻塞 UI 线程），但逐个 Task.Run 会在打开
/// 含大量图片的面板时产生解码风暴。这里用「单通道 + 卡片间让步」把并发压到 1，
/// 既保持界面响应，又避免内存峰值。
/// </remarks>
public sealed class ThumbnailLoadScheduler : IDisposable
{
    /// <summary>处理完一个 unit 后让出 UI 线程的时间，保证滚动与点击优先得到响应。</summary>
    private static readonly TimeSpan YieldDelay = TimeSpan.FromMilliseconds(24);

    private readonly ILogger<ThumbnailLoadScheduler> _logger;
    private readonly HashSet<Guid> _scheduled = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;

    /// <summary>创建调度器。</summary>
    public ThumbnailLoadScheduler(ILogger<ThumbnailLoadScheduler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 请求加载集合中尚未加载的图片缩略图。
    /// 重复调用是安全的：已排队或已完成的 unit 不会被重复处理。
    /// </summary>
    public void Request(IEnumerable<UnitViewModel> units)
    {
        ArgumentNullException.ThrowIfNull(units);

        CancellationTokenSource cts;
        lock (_gate)
        {
            var pending = units
                .Where(u => u.Kind == Domain.Units.UnitKind.Image && u.Thumbnail is null && _scheduled.Add(u.Id.Value))
                .ToList();

            if (pending.Count == 0)
            {
                return;
            }

            _logger.LogDebug("计划加载 {Count} 张缩略图（共 {Total} 个 unit）。", pending.Count, units.Count());

            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            cts = _pending;

            _ = RunAsync(pending, cts.Token);
        }
    }

    /// <summary>清除已排队记录（清空面板后让同 id 的卡片可以重新加载）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _scheduled.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task RunAsync(IReadOnlyList<UnitViewModel> units, CancellationToken token)
    {
        foreach (var unit in units)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await unit.EnsureImageLoadedAsync().ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单张失败不影响其余图片
                _logger.LogWarning(ex, "加载 unit {Id} 的缩略图失败。", unit.Id);
            }

            try
            {
                await Task.Delay(YieldDelay, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
