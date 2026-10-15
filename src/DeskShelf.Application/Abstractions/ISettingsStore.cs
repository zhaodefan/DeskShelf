using DeskShelf.Application.Settings;

namespace DeskShelf.Application.Abstractions;

/// <summary>应用设置读写。</summary>
public interface ISettingsStore
{
    /// <summary>当前设置快照（始终非空，缺失或损坏时返回默认值）。</summary>
    ShelfSettings Current { get; }

    /// <summary>重新从存储加载。</summary>
    Task<ShelfSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>保存设置并更新 <see cref="Current"/>。</summary>
    Task SaveAsync(ShelfSettings settings, CancellationToken cancellationToken = default);

    /// <summary>设置发生变化时触发（用于主题、热键、自启等即时生效）。</summary>
    event EventHandler<ShelfSettings>? Changed;
}
