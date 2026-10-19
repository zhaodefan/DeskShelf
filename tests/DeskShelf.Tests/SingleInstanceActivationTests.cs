using DeskShelf.App.Services;
using Xunit;

namespace DeskShelf.Tests;

/// <summary>
/// 单实例「再次启动唤醒面板」通道。
/// </summary>
/// <remarks>
/// 守护的真实缺陷：程序在后台运行时再点快捷方式，旧实现只弹「已经在运行」提示框就退出，
/// 用户视角就是「点了没反应 / 打不开」。
///
/// 每个用例使用**唯一事件名**：具名事件是系统级对象，句柄释放还有延迟，
/// 若共用名称会让用例通过全局状态互相干扰（曾因此出现偶发失败）。
/// </remarks>
public sealed class SingleInstanceActivationTests
{
    private static string UniqueName() => "DeskShelf.Tests." + Guid.NewGuid().ToString("N");

    [Fact]
    public void TrySignalExisting_ReturnsFalseWhenEventDoesNotExist()
    {
        // 用必然不存在的名称：断言通道在「没有运行实例」时不抛异常、返回 false
        Assert.False(SingleInstanceActivation.TrySignalExisting(UniqueName()));
    }

    [Fact]
    public void Initialize_ThenSignal_InvokesListener()
    {
        var name = UniqueName();
        using var activation = new SingleInstanceActivation(name);
        Assert.True(activation.Initialize());

        using var invoked = new ManualResetEventSlim(false);
        activation.Listen(() => invoked.Set());

        Assert.True(SingleInstanceActivation.TrySignalExisting(name));
        Assert.True(invoked.Wait(TimeSpan.FromSeconds(5)), "监听方应收到唤醒请求");
    }

    [Fact]
    public void Signal_BeforeListen_IsNotLost()
    {
        // 关键时序：程序刚启动、面板还没就绪时用户又点了快捷方式 ——
        // 自动重置事件会保持有信号，等监听注册后立刻被消费。
        var name = UniqueName();
        using var activation = new SingleInstanceActivation(name);
        Assert.True(activation.Initialize());

        Assert.True(SingleInstanceActivation.TrySignalExisting(name));

        using var invoked = new ManualResetEventSlim(false);
        activation.Listen(() => invoked.Set());

        Assert.True(invoked.Wait(TimeSpan.FromSeconds(5)), "先到的唤醒请求不应丢失");
    }

    [Fact]
    public void Listen_CanHandleRepeatedActivations()
    {
        // 用户可能反复点快捷方式：每次都应把面板叫出来
        var name = UniqueName();
        using var activation = new SingleInstanceActivation(name);
        Assert.True(activation.Initialize());

        var count = 0;
        using var first = new ManualResetEventSlim(false);
        activation.Listen(() =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                first.Set();
            }
        });

        Assert.True(SingleInstanceActivation.TrySignalExisting(name));
        Assert.True(first.Wait(TimeSpan.FromSeconds(5)));

        Assert.True(SingleInstanceActivation.TrySignalExisting(name));
        Assert.True(
            SpinWait.SpinUntil(() => Volatile.Read(ref count) >= 2, TimeSpan.FromSeconds(5)),
            "第二次唤醒也应被处理");
    }

    [Fact]
    public void Dispose_StopsListeningAndIsIdempotent()
    {
        var name = UniqueName();
        var activation = new SingleInstanceActivation(name);
        Assert.True(activation.Initialize());
        activation.Listen(() => { });

        activation.Dispose();
        activation.Dispose();   // 退出路径可能被调用多次，不应抛异常

        // 释放后信号不应再触发监听者
        var invoked = false;
        activation.Listen(() => invoked = true);
        _ = SingleInstanceActivation.TrySignalExisting(name);
        Thread.Sleep(100);
        Assert.False(invoked, "Dispose 之后不应再响应唤醒");
    }
}
