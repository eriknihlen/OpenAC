// src/AcDream.Plugin.Abstractions/IPluginLogger.cs
namespace AcDream.Plugin.Abstractions;

public interface IPluginLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}
