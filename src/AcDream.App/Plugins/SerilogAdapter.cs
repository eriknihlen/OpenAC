using AcDream.Plugin.Abstractions;

namespace AcDream.App.Plugins;

public sealed class SerilogAdapter : IPluginLogger
{
    private readonly Serilog.ILogger _log;
    public SerilogAdapter(Serilog.ILogger log) => _log = log;
    public void Info(string message) => _log.Information("{Message}", message);
    public void Warn(string message) => _log.Warning("{Message}", message);
    public void Error(string message, Exception? exception = null) => _log.Error(exception, "{Message}", message);
}
