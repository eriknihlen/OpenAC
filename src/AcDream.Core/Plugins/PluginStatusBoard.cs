using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Plugins;

/// <summary>
/// The status lines every plugin of one plugin session shares, keyed by the
/// publishing plugin's id. One per session, so the two clients keep it the
/// same way: the session hands each plugin a view that writes under that
/// plugin's id alone.
/// </summary>
internal sealed class PluginStatusBoard
{
    internal const int MaximumKeyLength = 128;
    internal const int MaximumValueLength = 4096;
    internal const int MaximumLinesPerPlugin = 256;

    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, string>> _lines =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Publish(string pluginId, string key, string? value) =>
        Publish(pluginId, key, value, writer: null);

    private bool Publish(string pluginId, string key, string? value, Scoped? writer)
    {
        if (string.IsNullOrWhiteSpace(pluginId)
            || string.IsNullOrWhiteSpace(key)
            || key.Length > MaximumKeyLength
            || value is { Length: > MaximumValueLength })
        {
            return false;
        }
        lock (_gate)
        {
            // Checked under the board's lock, so a write racing the unload
            // either lands before the lines are cleared or not at all.
            if (writer is { IsClosed: true })
                return false;
            if (value is null)
            {
                if (_lines.TryGetValue(pluginId, out Dictionary<string, string>? held))
                {
                    held.Remove(key);
                    if (held.Count == 0)
                        _lines.Remove(pluginId);
                }
                return true;
            }
            if (!_lines.TryGetValue(pluginId, out Dictionary<string, string>? lines))
                _lines[pluginId] = lines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!lines.ContainsKey(key) && lines.Count >= MaximumLinesPerPlugin)
                return false;
            lines[key] = value;
            return true;
        }
    }

    public bool TryRead(string pluginId, string key, out string value)
    {
        value = string.Empty;
        if (pluginId is null || key is null)
            return false;
        lock (_gate)
        {
            if (_lines.TryGetValue(pluginId, out Dictionary<string, string>? lines)
                && lines.TryGetValue(key, out string? found))
            {
                value = found;
                return true;
            }
            return false;
        }
    }

    public IReadOnlyDictionary<string, string> Capture(string pluginId)
    {
        if (pluginId is null)
            return new Dictionary<string, string>();
        lock (_gate)
        {
            return _lines.TryGetValue(pluginId, out Dictionary<string, string>? lines)
                ? new Dictionary<string, string>(lines, StringComparer.Ordinal)
                : new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// One plugin's view: it writes under its own id alone, and not at all
    /// once the plugin is unloaded -- a timer or socket callback that fires
    /// afterwards cannot put a line back.
    /// </summary>
    internal sealed class Scoped(PluginStatusBoard board, string pluginId)
        : IPluginStatusBoard
    {
        internal bool IsClosed { get; private set; }

        public bool IsAvailable => true;

        public bool Publish(string key, string? value) =>
            board.Publish(pluginId, key, value, this);

        /// <summary>Refuses every later write and drops the lines written.</summary>
        internal void Close()
        {
            lock (board._gate)
            {
                IsClosed = true;
                board._lines.Remove(pluginId);
            }
        }

        public bool TryRead(string pluginId, string key, out string value) =>
            board.TryRead(pluginId, key, out value);

        public IReadOnlyDictionary<string, string> Capture(string pluginId) =>
            board.Capture(pluginId);
    }
}
