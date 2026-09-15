using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.DrakBot.Tests.Fakes;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Remote.Tests;

/// <summary>A plugin host over the scriptable surface, with storage in memory and settings a test can set.</summary>
internal sealed class RemoteTestHost : IPluginHost
{
    public FakeAutomationSurface Surface { get; } = new();
    public MemoryStorage Storage { get; } = new();
    public PluginCommandRegistry Commands { get; } = new();
    public WorldEvents Events { get; } = new();
    public Dictionary<string, string> SessionSettings { get; } = new(StringComparer.Ordinal);
    public FakeLogger Log { get; } = new();

    public bool HasUi => false;
    IPluginLogger IPluginHost.Log => Log;
    IReadOnlyDictionary<string, string> IPluginHost.SessionSettings => SessionSettings;
    public IGameState State => new WorldGameState();
    IEvents IPluginHost.Events => Events;
    public ISelectionService Selection { get; } = new SelectionState();
    public IUiRegistry Ui => NoOpUiRegistry.Instance;
    IPluginCommandRegistry IPluginHost.Commands => Commands;
    IPluginStorage IPluginHost.Storage => Storage;
    public IAutomationSurface Automation => Surface;

    /// <summary>The bot and its remote, initialized the way a host does it, without the session.</summary>
    public (DrakBotPlugin Bot, DrakBotRemotePlugin Remote) Plugins(RemoteHostServices? services = null)
    {
        var bot = new DrakBotPlugin();
        bot.Initialize(this);
        var remote = new DrakBotRemotePlugin(bot, services);
        remote.Initialize(this);
        return (bot, remote);
    }
}

internal sealed class MemoryStorage : IPluginStorage
{
    public Dictionary<string, string> Text { get; } = new(StringComparer.Ordinal);
    public bool IsAvailable => true;
    public string? ReadText(string key) => Text.TryGetValue(key, out string? value) ? value : null;
    public IReadOnlyList<string> List(string prefix) => Text.Keys
        .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
        .OrderBy(static key => key, StringComparer.Ordinal)
        .ToArray();
    public void WriteText(string key, string content) => Text[key] = content;
    public bool Delete(string key) => Text.Remove(key);
}
