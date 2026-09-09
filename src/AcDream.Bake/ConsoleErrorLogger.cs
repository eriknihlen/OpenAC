using System;
using Microsoft.Extensions.Logging;

namespace AcDream.Bake;

public sealed class ConsoleErrorLogger : ILogger {
    private readonly string _categoryName;

    public ConsoleErrorLogger(string categoryName) {
        _categoryName = categoryName;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (!IsEnabled(logLevel)) return;
        var message = formatter(state, exception);
        Console.Error.WriteLine($"[{logLevel}] {_categoryName}: {message}");
        if (exception is not null) Console.Error.WriteLine(exception);
    }
}

public sealed class ConsoleErrorLoggerProvider : ILoggerProvider {
    public ILogger CreateLogger(string categoryName) => new ConsoleErrorLogger(categoryName);
    public void Dispose() { }
}
