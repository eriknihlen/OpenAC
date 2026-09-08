using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace AcDream.Content.Tests;

public static class ContentConformanceDats {
    public static string? ResolveDatDir() {
        var fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        var def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }
}

internal sealed class TestConsoleLogger : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
        if (!IsEnabled(logLevel)) return;
        Console.WriteLine($"[{logLevel}] MeshExtractor: {formatter(state, exception)}");
        if (exception is not null) Console.WriteLine(exception);
    }
}
