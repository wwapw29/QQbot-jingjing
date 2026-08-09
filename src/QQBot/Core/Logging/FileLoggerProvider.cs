using Microsoft.Extensions.Logging;

namespace QQBot.Core.Logging;

/// <summary>
/// 按天落盘的文件日志记录器（后台面板「Debug 日志」页数据源）。
/// - 文件：{LogsDir}/{yyyy-MM-dd}.log，保留 LogRetentionDays 天（启动时清理过期文件）。
/// - 格式：yyyy-MM-dd HH:mm:ss.fff [LVL] 消息（多行消息用行内 \n 缩进续行，便于逐行解析/过滤）。
/// - 级别：全量记录（含 Debug/Trace），是否详细由业务代码控制（如 Bot.Debug 开关）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDir;
    private readonly int _retentionDays;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = "";
    private DateTime _lastCleanup = DateTime.MinValue;

    public FileLoggerProvider(string logsDir, int retentionDays = 7)
    {
        _logsDir = logsDir;
        _retentionDays = Math.Max(1, retentionDays);
        try
        {
            Directory.CreateDirectory(_logsDir);
            Cleanup();
        }
        catch { /* 日志目录不可写则静默（不影响主程序） */ }
    }

    /// <summary>删除保留天数之外的日志文件（启动与每日首次写入时调用）</summary>
    private void Cleanup()
    {
        if (_retentionDays <= 0) return;
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var f in Directory.GetFiles(_logsDir, "*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                {
                    try { File.Delete(f); } catch { }
                }
            }
            _lastCleanup = DateTime.Now;
        }
        catch { }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    /// <summary>写一行（线程安全；按日期切换文件）</summary>
    internal void Write(DateTime now, string level, string message, string? exception)
    {
        var date = now.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            try
            {
                if (date != _currentDate || _writer is null)
                {
                    _writer?.Dispose();
                    // FileShare.ReadWrite：允许后台面板同时读取日志文件
                    _writer = new StreamWriter(
                        new FileStream(Path.Combine(_logsDir, date + ".log"),
                            FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                        System.Text.Encoding.UTF8) { AutoFlush = true };
                    _currentDate = date;
                    // 每日首次写入顺带清理过期文件（防长期运行不重启）
                    if ((now - _lastCleanup).TotalDays >= 1) Cleanup();
                }
                _writer.WriteLine($"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
                if (!string.IsNullOrEmpty(exception)) _writer.WriteLine(exception);
            }
            catch { /* 写日志失败不影响主流程 */ }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>单条日志（文件名 = 格式无关；实际按 provider 的按天文件写）</summary>
    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;

        public FileLogger(FileLoggerProvider provider, string category) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;   // 全量记录

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                LogLevel.Critical => "CRT",
                _ => "???"
            };
            var msg = formatter(state, exception);
            if (msg.Contains('\n'))
            {
                // 多行消息：续行用「  | 」前缀缩进，保证按行解析时级别/时间戳只出现在首行
                msg = msg.Replace("\r\n", "\n").Replace("\n", "\n  | ");
            }
            _provider.Write(DateTime.Now, level, msg, exception is null ? null : exception.ToString());
        }
    }
}
