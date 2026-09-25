using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

/// <summary>
/// One plugin says what state it is in and another reads it, without either
/// knowing the other: each writes under its own id alone, any plugin reads
/// any line, and a plugin's lines go when it is unloaded.
///
/// Mutation checks (2026-09-25): writing under the reader's id instead of
/// the scope's turned <see cref="EachPluginWritesUnderItsOwnIdAndReadsAnyones"/>
/// red; skipping the clear on dispose turned
/// <see cref="APluginsLinesGoWhenItIsUnloaded"/> red.
/// </summary>
public sealed class PluginStatusBoardTests
{
    [Fact]
    public void EachPluginWritesUnderItsOwnIdAndReadsAnyones()
    {
        var board = new PluginStatusBoard();
        IPluginStatusBoard bot = new PluginStatusBoard.Scoped(board, "acdream.bot");
        IPluginStatusBoard reporter = new PluginStatusBoard.Scoped(board, "acdream.reporter");

        Assert.True(bot.IsAvailable);
        Assert.True(bot.Publish("state", "Combat"));
        Assert.True(reporter.Publish("state", "Idle"));

        Assert.True(reporter.TryRead("ACDREAM.BOT", "state", out string botState));
        Assert.Equal("Combat", botState);
        Assert.True(bot.TryRead("acdream.reporter", "state", out string reporterState));
        Assert.Equal("Idle", reporterState);
        Assert.False(reporter.TryRead("acdream.bot", "State", out string missing));
        Assert.Equal(string.Empty, missing);
        Assert.Equal(
            new Dictionary<string, string> { ["state"] = "Combat" },
            reporter.Capture("acdream.bot"));

        Assert.True(bot.Publish("state", null));
        Assert.False(reporter.TryRead("acdream.bot", "state", out _));
        Assert.Empty(reporter.Capture("acdream.bot"));
        Assert.True(bot.Publish("never-set", null));
    }

    [Fact]
    public void ALineIsRefusedWhenItCannotBeKept()
    {
        var board = new PluginStatusBoard();
        IPluginStatusBoard bot = new PluginStatusBoard.Scoped(board, "acdream.bot");

        Assert.False(bot.Publish(" ", "x"));
        Assert.False(bot.Publish(new string('k', 129), "x"));
        Assert.False(bot.Publish("state", new string('v', 4097)));
        for (int index = 0; index < PluginStatusBoard.MaximumLinesPerPlugin; index++)
            Assert.True(bot.Publish("line" + index, "x"));
        Assert.False(bot.Publish("one-too-many", "x"));
        // Replacing a line that is there is not a new line.
        Assert.True(bot.Publish("line0", "y"));
    }

    [Fact]
    public void APluginsLinesGoWhenItIsUnloaded()
    {
        var board = new PluginStatusBoard();
        var host = new MinimalHost();
        var bot = new ScopedPluginHost(host, "acdream.bot", "Bot", null, board);
        using var reporter = new ScopedPluginHost(host, "acdream.reporter", "Reporter", null, board);

        Assert.True(bot.StatusBoard.Publish("state", "Combat"));
        Assert.True(reporter.StatusBoard.TryRead("acdream.bot", "state", out _));

        IPluginStatusBoard kept = bot.StatusBoard;
        bot.Dispose();

        Assert.False(reporter.StatusBoard.TryRead("acdream.bot", "state", out _));
        // A callback that outlives the plugin cannot put a line back.
        Assert.False(kept.Publish("state", "Late"));
        Assert.False(reporter.StatusBoard.TryRead("acdream.bot", "state", out _));
    }

    [Fact]
    public void AScopeWithoutASessionBoardFallsBackToTheHosts()
    {
        using var scope = new ScopedPluginHost(new MinimalHost(), "acdream.bot", "Bot");
        Assert.Same(NoOpPluginStatusBoard.Instance, scope.StatusBoard);
        Assert.False(scope.StatusBoard.IsAvailable);
        Assert.False(scope.StatusBoard.Publish("state", "x"));
        Assert.False(scope.StatusBoard.TryRead("acdream.bot", "state", out _));
        Assert.Empty(scope.StatusBoard.Capture("acdream.bot"));
    }

    private sealed class MinimalHost : IPluginHost
    {
        public bool HasUi => false;
        public IPluginLogger Log { get; } = new NullLog();
        public IGameState State => throw new NotSupportedException();
        public IEvents Events { get; } = new NoEvents();
        public ISelectionService Selection { get; } = new AcDream.Core.Selection.SelectionState();
        public IUiRegistry Ui => NoOpUiRegistry.Instance;
        public IAutomationSurface Automation => NoOpAutomationSurface.Instance;
    }

    private sealed class NullLog : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class NoEvents : IEvents
    {
        public event Action<WorldEntitySnapshot> EntitySpawned { add { } remove { } }
        public event Action<double> Tick { add { } remove { } }
    }
}
