namespace DeskShelf.App.Services;

/// <summary>
/// 主题应用入口（表现层抽象）。
/// 设置窗口切换主题时需要即时预览，但主题字典由 <c>App</c> 持有，
/// 因此把「应用主题」这一动作抽象出来注入给 ViewModel，避免 ViewModel 反向依赖 App。
/// </summary>
public interface IThemeApplier
{
    /// <summary>按当前设置重新应用主题（读取最新的 <c>ISettingsStore.Current</c>）。</summary>
    void ApplyFromSettings();

    /// <summary>临时预览指定明暗（不写入设置，用于设置窗口的即时预览）。</summary>
    /// <param name="light">是否浅色。</param>
    void Preview(bool light);
}
