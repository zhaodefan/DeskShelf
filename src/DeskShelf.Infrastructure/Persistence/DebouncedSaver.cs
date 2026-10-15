namespace DeskShelf.Infrastructure.Persistence;

/// <summary>
/// 防抖保存调度器：面板内容每次变化都重建计时器，只有静默一段时间后才真正落盘。
/// 作用是把「连续粘贴 10 条」压缩成 1 次磁盘写入。
/// </summary>
public sealed class DebouncedSaver : IDisposable
{
    private readonly Func<CancellationToken, Task> _save;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();

    private CancellationTokenSource? _pending;
    private Task _lastSave = Task.CompletedTask;
    private bool _disposed;

    /// <summary>创建调度器。</summary>
    /// <param name="save">实际执行保存的委托。</param>
    /// <param name="delay">静默间隔，默认 500ms。</param>
    public DebouncedSaver(Func<CancellationToken, Task> save, TimeSpan? delay = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _delay = delay ?? TimeSpan.FromMilliseconds(500);
    }

    /// <summary>请求一次（延迟）保存。</summary>
    public void Request()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending?.Cancel();
            _pending?.Dispose();
            _pending = new CancellationTokenSource();
            var token = _pending.Token;
            _lastSave = RunAsync(token);
        }
    }

    /// <summary>立即保存并等待完成（退出前调用，保证不丢数据）。</summary>
    public async Task FlushAsync()
    {
        CancellationTokenSource? pending;
        Task inFlight;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
            inFlight = _lastSave;
        }

        pending?.Cancel();
        pending?.Dispose();

        try
        {
            await inFlight.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 被后续请求取代，属正常流程
        }

        await _save(CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_delay, token).ConfigureAwait(false);
            await _save(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 有新的变更到来，本次保存被取消
        }
        catch (Exception)
        {
            // 保存失败通过注入的委托内部记录日志，这里不再抛出（避免未观测的任务异常）
        }
    }
}
