using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DeskTools.Infrastructure.Logging;

/// <summary>
/// 极简滚动文件日志：写入 <c>%APPDATA%\DeskTools\logs\desktools-yyyyMMdd.log</c>，
/// 保留最近 N 天。存在的理由是剪贴板/热键这类系统级故障在用户机器上难以复现，
/// 必须有落盘日志可查。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly BlockingCollection<string> _queue = new(new ConcurrentQueue<string>(), boundedCapacity: 4096);
    private readonly Task _writer;
    private readonly LogLevel _minimumLevel;

    private bool _disposed;

    /// <summary>创建文件日志提供程序。</summary>
    /// <param name="directory">日志目录。</param>
    /// <param name="minimumLevel">最低记录级别。</param>
    /// <param name="retentionDays">保留天数。</param>
    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retentionDays = 7)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _minimumLevel = minimumLevel;
        _retentionDays = retentionDays;
        Directory.CreateDirectory(_directory);
        _writer = Task.Run(WriteLoop);
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 退出阶段的写入失败无需处理
        }

        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (_disposed || _queue.IsAddingCompleted)
        {
            return;
        }

        // 队列满时丢弃（日志不能反过来拖慢 UI）
        _queue.TryAdd(line);
    }

    private void WriteLoop()
    {
        var builder = new StringBuilder();
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                builder.Clear();
                builder.Append(line).Append('\n');
                var file = Path.Combine(_directory, $"desktools-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, builder.ToString(), Encoding.UTF8);
                CleanupOldFiles();
            }
            catch (Exception)
            {
                // 日志自身的失败绝不冒泡
            }
        }
    }

    private void CleanupOldFiles()
    {
        try
        {
            var threshold = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "desktools-*.log"))
            {
                if (File.GetLastWriteTime(file) < threshold)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception)
        {
            // 忽略清理失败
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        public FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            var message = formatter(state, exception);
            var shortCategory = _category.Split('.')[^1];
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{Level(logLevel)}] {shortCategory}: {message}";
            if (exception is not null)
            {
                // 记录完整异常（含堆栈），否则排查系统级问题将无从下手
                line += $" | {exception}";
            }

            _provider.Enqueue(line);
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}
