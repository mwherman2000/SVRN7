using System;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Web7.SVRN7.Apps
{
    /// <summary>
    /// Shared logging entry point for PandoMail. Consolidates what used to be scattered
    /// System.Diagnostics.Debug.WriteLine calls (visible only with an attached debugger)
    /// onto Microsoft.Extensions.Logging, matching the Citizen TDA's own logging model.
    /// No DI container needed for a WinForms app this size — LoggerFactory is created
    /// once, directly, and classes pull their own ILogger&lt;T&gt; from it.
    /// </summary>
    internal static class AppLog
    {
        private static readonly ILoggerFactory _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(new FileLoggerProvider(
                Path.Combine(AppContext.BaseDirectory, "logs", "pandomail.log")));
        });

        public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
    }

    /// <summary>
    /// Log-category marker for process-lifetime events (startup, global exception
    /// handlers) that aren't naturally owned by one class — a static class like
    /// <c>Program</c> cannot itself be used as an <see cref="ILogger{T}"/> type argument.
    /// </summary>
    internal sealed class AppLifecycle { }

    /// <summary>
    /// Minimal file-backed ILoggerProvider — appends timestamped lines to a single log
    /// file. No rotation, no buffering: PandoMail's traffic volume (WebSocket connect/
    /// send/receive events) doesn't warrant either, and simplicity here means one less
    /// thing to get wrong in a diagnostic path that's supposed to help when things break.
    /// </summary>
    internal sealed class FileLoggerProvider : ILoggerProvider
    {
        private readonly string _filePath;
        private readonly object _writeLock = new();

        public FileLoggerProvider(string filePath)
        {
            _filePath = filePath;
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        }

        public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _filePath, _writeLock);

        public void Dispose() { }

        private sealed class FileLogger : ILogger
        {
            private readonly string _category;
            private readonly string _filePath;
            private readonly object _writeLock;

            public FileLogger(string category, string filePath, object writeLock)
            {
                _category  = category;
                _filePath  = filePath;
                _writeLock = writeLock;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (!IsEnabled(logLevel)) return;

                var line = new StringBuilder()
                    .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(logLevel).Append("] ")
                    .Append(_category).Append(": ")
                    .Append(formatter(state, exception));
                if (exception is not null)
                    line.Append(Environment.NewLine).Append(exception);

                lock (_writeLock)
                {
                    try { File.AppendAllText(_filePath, line.ToString() + Environment.NewLine); }
                    catch { /* best-effort — a logging failure must never crash the app */ }
                }
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
