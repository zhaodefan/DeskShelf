using DeskShelf.Application.Settings;

namespace DeskShelf.Application.Abstractions;

/// <summary>
/// 全局热键服务（Alt+X 呼出/关闭面板）。
/// </summary>
public interface IHotkeyService
{
    /// <summary>当前是否已成功注册。</summary>
    bool IsRegistered { get; }

    /// <summary>当前生效的手势（未注册时为 <c>null</c>）。</summary>
    HotkeyGesture? RegisteredGesture { get; }

    /// <summary>
    /// 注册（或重新注册）热键。注册失败（被其他程序占用）时返回 <c>false</c> 而不抛异常，
    /// 由上层提示用户改键。
    /// </summary>
    bool TryRegister(HotkeyGesture gesture);

    /// <summary>注销当前热键；未注册时为空操作。</summary>
    void Unregister();

    /// <summary>热键被按下时触发（已在 UI 线程外，调用方需自行切线程）。</summary>
    event EventHandler? Pressed;
}
