// src/AcDream.Plugin.Abstractions/IPluginLogger.cs
namespace AcDream.Plugin.Abstractions;

/// <summary>
/// Writes to the client's own log on the plugin's behalf. Lines are tagged
/// with the plugin so a user can tell whose message they are reading.
/// </summary>
public interface IPluginLogger
{
    /// <summary>Records an ordinary informational line.</summary>
    void Info(string message);

    /// <summary>Records something that went wrong but did not stop the plugin.</summary>
    void Warn(string message);

    /// <summary>
    /// Records a failure, optionally with the exception that caused it.
    /// </summary>
    void Error(string message, Exception? exception = null);
}
