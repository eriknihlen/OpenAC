using AcDream.Headless.Diagnostics;
using AcDream.Plugin.Abstractions;

namespace AcDream.Headless.Plugins;

internal sealed class HeadlessPluginLogger : IPluginLogger
{
    private readonly HeadlessDiagnosticWriter _diagnostics;
    private readonly string _sessionId;
    private readonly Func<ulong> _generation;

    internal HeadlessPluginLogger(
        HeadlessDiagnosticWriter diagnostics,
        string sessionId,
        Func<ulong> generation)
    {
        _diagnostics = diagnostics
            ?? throw new ArgumentNullException(nameof(diagnostics));
        _sessionId = sessionId
            ?? throw new ArgumentNullException(nameof(sessionId));
        _generation = generation
            ?? throw new ArgumentNullException(nameof(generation));
    }

    public void Info(string message) => Write("info", message);
    public void Warn(string message) => Write("warn", message);

    public void Error(string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            _diagnostics.Failure(_sessionId, "plugin", exception);
            return;
        }
        Write("error", message);
    }

    private void Write(string level, string message) =>
        _diagnostics.Message(
            _sessionId,
            $"plugin-{level}:{message}",
            _generation());
}
