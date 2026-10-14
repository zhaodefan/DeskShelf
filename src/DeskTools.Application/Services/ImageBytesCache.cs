using DeskTools.Application.Abstractions;
using DeskTools.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace DeskTools.Application.Services;

/// <summary>
/// 图片字节的内存缓存（LRU，容量固定）。
/// 目的：滚动包含大量图片 unit 的面板时，避免反复读盘与重复解码；
/// 容量封顶保证内存占用可预测。
/// </summary>
public sealed class ImageBytesCache
{
    /// <summary>默认缓存条目数上限。</summary>
    public const int DefaultCapacity = 96;

    private readonly IImageBytesSource _source;
    private readonly ILogger<ImageBytesCache> _logger;
    private readonly LinkedList<StorageKey> _order = new();
    private readonly Dictionary<StorageKey, LinkedListNode<StorageKey>> _nodes = new();
    private readonly Dictionary<StorageKey, byte[]?> _values = new();
    private readonly object _gate = new();

    /// <summary>创建缓存。</summary>
    public ImageBytesCache(IImageBytesSource source, ILogger<ImageBytesCache> logger, int capacity = DefaultCapacity)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Capacity = capacity < 1 ? 1 : capacity;
    }

    /// <summary>缓存条目上限。</summary>
    public int Capacity { get; }

    /// <summary>只读缓存命中（不触发 IO），未命中返回 <c>false</c>。</summary>
    public bool TryGetCached(StorageKey key, out byte[]? bytes)
    {
        lock (_gate)
        {
            if (_values.TryGetValue(key, out bytes))
            {
                Touch(key);
                return true;
            }

            return false;
        }
    }

    /// <summary>获取图片字节；未缓存时从存储读取并写入缓存。读取失败缓存为 <c>null</c> 以避免反复重试。</summary>
    public async Task<byte[]?> GetAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        if (key.IsEmpty)
        {
            return null;
        }

        if (TryGetCached(key, out var cached))
        {
            return cached;
        }

        byte[]? bytes;
        try
        {
            bytes = await _source.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取图片字节失败：{Key}", key.Value);
            bytes = null;
        }

        Store(key, bytes);
        return bytes;
    }

    /// <summary>清空缓存（例如清空面板或重新加载数据后）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _nodes.Clear();
            _values.Clear();
        }
    }

    private void Store(StorageKey key, byte[]? bytes)
    {
        lock (_gate)
        {
            _values[key] = bytes;
            Touch(key);

            while (_values.Count > Capacity && _order.Last is { } last)
            {
                _values.Remove(last.Value);
                _nodes.Remove(last.Value);
                _order.RemoveLast();
            }
        }
    }

    private void Touch(StorageKey key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            _order.Remove(node);
        }

        _nodes[key] = _order.AddFirst(key);
    }
}
