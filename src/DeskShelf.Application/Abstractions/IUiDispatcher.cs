namespace DeskShelf.Application.Abstractions;

/// <summary>
/// UI 线程调度抽象。领域层与用例层完全不感知线程模型，
/// 所有需要回到 UI 线程的回调都经由此接口。
/// </summary>
public interface IUiDispatcher
{
    /// <summary>当前是否已在 UI 线程。</summary>
    bool IsOnUiThread { get; }

    /// <summary>若已在 UI 线程则同步执行，否则投递到 UI 线程异步执行。</summary>
    void Post(Action action);

    /// <summary>投递到 UI 线程并等待完成。</summary>
    Task InvokeAsync(Action action);
}
