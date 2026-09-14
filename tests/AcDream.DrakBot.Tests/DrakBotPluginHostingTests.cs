using AcDream.DrakBot.Tests.Fakes;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Tests;

/// <summary>The bot hosted the way the client hosts it: as a built-in through <see cref="PluginSession"/>.</summary>
public sealed class DrakBotPluginHostingTests
{
    [Fact]
    public void BuiltInBotLoadsRegistersItsCommandAndTicks()
    {
        var host = new StubHost();
        var statuses = new List<PluginSessionStatus>();
        using var session = new PluginSession(host, statuses.Add);
        var plugin = new DrakBotPlugin();

        session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, plugin));
        Assert.Empty(statuses); // nothing is enabled until the host starts
        Assert.Null(plugin.Engine);
        session.Start([], allowList: null);

        PluginSessionStatus status = Assert.Single(statuses);
        Assert.Equal(PluginSessionStatusKind.Loaded, status.Kind);
        Assert.Equal([DrakBotPlugin.Id], session.LoadedPluginIds);
        Assert.NotNull(plugin.Engine);

        Assert.True(host.Commands.TryHandle("/drakbot start"));
        Assert.True(plugin.Engine.IsRunning);
        Assert.Contains("DrakBot started", host.Surface.SystemMessages);

        host.Events.FireTick(0.1);
        Assert.Equal("nothing to do", plugin.Engine.LastReason);

        Assert.True(host.Commands.TryHandle("/bot stop")); // the short alias
        Assert.False(plugin.Engine.IsRunning);
    }

    [Fact]
    public void DisposingTheSessionDisablesTheBot()
    {
        var host = new StubHost();
        var session = new PluginSession(host);
        var plugin = new DrakBotPlugin();
        session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, plugin));
        session.Start([], allowList: null);
        host.Commands.TryHandle("/drakbot start");

        session.Dispose();

        Assert.False(plugin.Engine!.IsRunning);
        Assert.False(host.Commands.TryHandle("/drakbot status"));
    }

    [Fact]
    public void BuiltInsMustBeAddedBeforeStart()
    {
        var host = new StubHost();
        using var session = new PluginSession(host);
        session.Start([], allowList: []);

        Assert.Throws<InvalidOperationException>(() =>
            session.AddBuiltIn(new BuiltInPlugin("x", "X", "1", new DrakBotPlugin())));
    }

    [Fact]
    public void ProfilesRoundTripThroughPluginStorage()
    {
        var host = new StubHost();
        using var session = new PluginSession(host);
        var plugin = new DrakBotPlugin();
        session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, plugin));
        session.Start([], allowList: null);

        Assert.True(host.Commands.TryHandle("/drakbot style magic"));
        Assert.True(host.Commands.TryHandle("/drakbot profile save hunting"));
        Assert.True(host.Commands.TryHandle("/drakbot profile reset"));
        Assert.Equal(Profiles.CombatStyle.Melee, plugin.Engine!.Profile.Combat.Style);
        Assert.True(host.Commands.TryHandle("/drakbot profile load hunting"));

        Assert.Equal(Profiles.CombatStyle.Magic, plugin.Engine.Profile.Combat.Style);
        Assert.Equal("hunting", plugin.Engine.Profile.Name);
        Assert.Contains(host.Storage.Text.Keys, key => key.EndsWith("hunting.json", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionSettingsLoadTheNamedProfileAndSetPatrolOnLogin()
    {
        var host = new StubHost();
        using (var session = new PluginSession(host))
        {
            var plugin = new DrakBotPlugin();
            session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, plugin));
            session.Start([], allowList: null);
            Assert.True(host.Commands.TryHandle("/drakbot style magic"));
            Assert.True(host.Commands.TryHandle("/drakbot profile save hunting"));
            Assert.True(host.Commands.TryHandle("/drakbot profile reset"));
        }

        host.SessionSettings[DrakBotPlugin.ProfileSetting] = "hunting";
        host.SessionSettings[DrakBotPlugin.PatrolOnLoginSetting] = "true";
        using var next = new PluginSession(host);
        var bot = new DrakBotPlugin();
        next.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, bot));
        next.Start([], allowList: null);

        Assert.Equal("hunting", bot.Engine!.Profile.Name);
        Assert.Equal(Profiles.CombatStyle.Magic, bot.Engine.Profile.Combat.Style);
        Assert.True(bot.Engine.Profile.Navigation.PatrolOnLogin);
    }

    [Fact]
    public void SessionSettingsNamingAMissingProfileLeaveTheBotOnItsOwn()
    {
        var host = new StubHost();
        host.SessionSettings[DrakBotPlugin.ProfileSetting] = "nowhere";
        host.SessionSettings[DrakBotPlugin.PatrolOnLoginSetting] = "sometimes";
        using var session = new PluginSession(host);
        var plugin = new DrakBotPlugin();
        session.AddBuiltIn(new BuiltInPlugin(DrakBotPlugin.Id, DrakBotPlugin.DisplayName, DrakBotPlugin.Version, plugin));
        session.Start([], allowList: null);

        Assert.Equal(Profiles.BotProfile.Default.Name, plugin.Engine!.Profile.Name);
        Assert.False(plugin.Engine.Profile.Navigation.PatrolOnLogin);
        Assert.Contains(host.Log.Lines, w => w.StartsWith("warn:", StringComparison.Ordinal) && w.Contains("nowhere", StringComparison.Ordinal));
        Assert.Contains(host.Log.Lines, w => w.StartsWith("warn:", StringComparison.Ordinal) && w.Contains("sometimes", StringComparison.Ordinal));
    }

    private sealed class StubHost : IPluginHost
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
    }

    private sealed class MemoryStorage : IPluginStorage
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
}
