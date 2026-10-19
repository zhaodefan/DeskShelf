namespace DeskShelf.App.Services;

/// <summary>
/// 单实例的「再次启动」唤醒通道。
/// </summary>
/// <remarks>
/// 解决的问题：程序已在后台运行时（面板可能已收起、托盘图标在折叠区里找不到），
/// 用户再点桌面/开始菜单快捷方式，期望的是**把面板叫出来**。
/// 早先的实现只弹一个「已经在运行」的提示框就退出了，从用户视角就是「点了没反应、打不开」。
///
/// 实现方式：具名事件（<c>Global\DeskShelf.ShowPanel</c>）。
/// <list type="number">
/// <item>首个实例创建事件并注册等待；</item>
/// <item>后启动的实例 <see cref="TrySignalExisting"/> 触发事件后静默退出；</item>
/// <item>首实例收到信号 → 在 UI 线程显示面板。</item>
/// </list>
/// 用自动重置事件：在还没有等待者时设置，事件会保持有信号状态，等首实例注册等待后立刻消费 ——
/// 因此「程序刚启动、用户又点了快捷方式」这种时序也不会丢信号。
/// </remarks>
internal sealed class SingleInstanceActivation : IDisposable
{
    /// <summary>唤醒事件名（与单实例互斥体同前缀，便于识别）。</summary>
    internal const string EventName = @"Global\DeskShelf.ShowPanel";

    private EventWaitHandle? _signal;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;
    private readonly string _eventName;

    /// <summary>创建唤醒通道。</summary>
    /// <param name="eventName">
    /// 事件名；生产代码使用默认值，测试可传入唯一名称以避免用例之间通过全局对象互相干扰。
    /// </param>
    public SingleInstanceActivation(string? eventName = null)
    {
        _eventName = string.IsNullOrWhiteSpace(eventName) ? EventName : eventName;
    }

    /// <summary>
    /// 在当前进程中创建（或打开）唤醒事件。必须在单实例检查通过后调用。
    /// </summary>
    /// <returns>是否成功创建；失败时功能降级，不影响程序其它部分。</returns>
    public bool Initialize()
    {
        try
        {
            _signal = new EventWaitHandle(
                initialState: false,
                EventResetMode.AutoReset,
                _eventName,
                out _);

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            _signal = null;
            return false;
        }
    }

    /// <summary>
    /// 开始监听唤醒请求。建议在面板已创建之后调用。
    /// </summary>
    /// <param name="onActivate">收到唤醒请求时执行（会在**线程池线程**上被调用，调用方需自行切回 UI 线程）。</param>
    public void Listen(Action onActivate)
    {
        ArgumentNullException.ThrowIfNull(onActivate);

        if (_signal is null || _disposed)
        {
            return;
        }

        // executeOnlyOnce: false —— 每次事件被触发都重新登记，支持反复唤醒
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _signal,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    onActivate();
                }
            },
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// 通知已在运行的实例显示面板。
    /// </summary>
    /// <param name="eventName">事件名；生产代码使用默认值，测试可指定唯一名称。</param>
    /// <returns>
    /// <c>true</c> 表示成功发出唤醒请求（调用方应静默退出）；
    /// <c>false</c> 表示没找到运行中的实例或无权访问（调用方应给出提示）。
    /// </returns>
    public static bool TrySignalExisting(string? eventName = null)
    {
        var name = string.IsNullOrWhiteSpace(eventName) ? EventName : eventName;

        try
        {
            if (!EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                return false;
            }

            using (handle)
            {
                return handle.Set();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _registration?.Unregister(waitObject: null);
        }
        catch (Exception)
        {
            // 忽略：退出路径上的清理失败不影响关闭
        }

        _registration = null;
        _signal?.Dispose();
        _signal = null;
    }
}
