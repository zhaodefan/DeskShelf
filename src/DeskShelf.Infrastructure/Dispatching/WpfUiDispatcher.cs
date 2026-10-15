using System.Windows.Threading;
using DeskShelf.Application.Abstractions;

namespace DeskShelf.Infrastructure.Dispatching;

/// <summary>
/// 基于 WPF <see cref="Dispatcher"/> 的 UI 线程调度实现。
/// 这是「领域/用例层不感知线程」这一约束的落点：只有基础设施层知道 Dispatcher 的存在。
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    /// <summary>使用当前线程的 Dispatcher 构造（必须在 UI 线程调用）。</summary>
    public WpfUiDispatcher()
        : this(Dispatcher.CurrentDispatcher)
    {
    }

    /// <summary>使用指定 Dispatcher 构造。</summary>
    public WpfUiDispatcher(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <inheritdoc />
    public bool IsOnUiThread => _dispatcher.CheckAccess();

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsOnUiThread)
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }

    /// <inheritdoc />
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return IsOnUiThread
            ? RunInline(action)
            : _dispatcher.InvokeAsync(action, DispatcherPriority.Normal).Task;
    }

    private static Task RunInline(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
