#pragma warning disable 8633

using System.Text;

namespace FileSyncService.Extensions;

public static class FileLoggerExtensions
{
    public static ILoggingBuilder AddFile(this ILoggingBuilder builder, string filePath, int maxSizeMb = 10, int maxFiles = 5)
    {
        builder.AddProvider(new RotatingFileLoggerProvider(filePath, maxSizeMb, maxFiles));
        return builder;
    }

    private class RotatingFileLoggerProvider : ILoggerProvider
    {
        private readonly string _filePath;
        private readonly int _maxSizeBytes;
        private readonly int _maxFiles;
        private readonly object _lock = new();

        public RotatingFileLoggerProvider(string filePath, int maxSizeMb, int maxFiles)
        {
            _filePath = filePath;
            _maxSizeBytes = maxSizeMb * 1024 * 1024;
            _maxFiles = maxFiles;

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }

        public ILogger CreateLogger(string categoryName) =>
            new RotatingFileLogger(_filePath, _maxSizeBytes, _maxFiles, _lock);

        public void Dispose() { }

        private class RotatingFileLogger(string path, int maxBytes, int maxFiles, object lockObj) : ILogger
        {
            private readonly string _path = path;
            private readonly int _maxBytes = maxBytes;
            private readonly int _maxFiles = maxFiles;
            private readonly object _lock = lockObj;

            public IDisposable BeginScope<TState>(TState state) => default!;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {formatter(state, exception)}";
                if (exception != null) msg += $" EX: {exception.Message}";

                lock (_lock)
                {
                    RotateIfNeeded();
                    File.AppendAllText(_path, msg + Environment.NewLine, Encoding.UTF8);
                }
            }

            private void RotateIfNeeded()
            {
                try
                {
                    var info = new FileInfo(_path);
                    if (!info.Exists || info.Length < _maxBytes) return;

                    // удаляем самый старый, если превышает лимит
                    if (_maxFiles > 0)
                    {
                        var oldest = $"{_path}.{_maxFiles}";
                        if (File.Exists(oldest))
                            File.Delete(oldest);

                        // сдвигаем файлы
                        for (int i = _maxFiles - 1; i >= 1; i--)
                        {
                            var src = $"{_path}.{i}";
                            var dst = $"{_path}.{i + 1}";
                            if (File.Exists(src))
                            {
                                File.Move(src, dst, true);
                            }
                        }

                        File.Move(_path, $"{_path}.1", true);
                    }
                }
                catch
                {
                    // не падаем из-за ошибок ротации
                }
            }
        }
    }
}
