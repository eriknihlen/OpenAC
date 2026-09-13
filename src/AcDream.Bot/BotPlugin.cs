using AcDream.Bot.Behaviors;
using AcDream.Bot.Profiles;
using AcDream.Bot.Spells;
using AcDream.Bot.Ui;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot;

/// <summary>
/// The built-in bot as the host sees it: a gameplay plugin that ticks the
/// engine, owns the <c>/bot</c> command and its windows. Nothing here touches
/// client internals; every action goes through <see cref="IAutomationSurface"/>.
/// </summary>
public sealed class BotPlugin : IAcDreamPlugin
{
    public const string Id = "acdream.bot";
    public const string DisplayName = "Bot";
    public const string Version = "0.1.0";

    private IPluginHost? _host;
    private BotEngine? _engine;
    private BotController? _controller;
    private BotCommands? _commands;
    private BotDashboard? _dashboard;
    private IDisposable? _commandLease;
    private IDisposable? _panelLease;
    private IDisposable? _drawerLease;
    private bool _enabled;

    public BotEngine? Engine => _engine;

    public BotController? Controller => _controller;

    public void Initialize(IPluginHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;

        IAutomationSurface surface = host.Automation;
        var clock = new TickClock();
        var cooldowns = new CastCooldowns(clock);
        var spells = new SpellSelector(surface.Spells, surface.Magic, cooldowns.IsOnCooldown);
        CastTracker Casts() => new(surface.Magic, clock, cooldowns);
        // Behaviors read the live profile through the engine, which does not
        // exist until they do; the closures resolve it lazily.
        BotEngine engine = null!;
        var navigation = new NavigationBehavior(() => engine.Profile.Navigation);
        var buffs = new SelfBuffBehavior(spells, Casts(), () => engine.Profile.Buffs);
        IBehavior[] behaviors =
        [
            new VitalRechargeBehavior(spells, Casts(), () => engine.Profile.Vitals),
            buffs,
            new CombatBehavior(spells, Casts(), () => engine.Profile.Combat),
            new LootBehavior(() => engine.Profile.Loot),
            navigation,
        ];
        engine = new BotEngine(surface, host.Log, behaviors, clock);
        _engine = engine;

        var store = new BotStore(host.Storage);
        BotProfile? saved = store.IsAvailable ? TryLoadDefault(store, host.Log) : null;
        if (saved is not null)
            _engine.Profile = saved;

        _controller = new BotController(
            _engine,
            store,
            navigation,
            buffs,
            () => surface.Navigation.Snapshot);
        _commands = new BotCommands(_controller, surface.Chat);
        _dashboard = new BotDashboard(_controller, surface);
    }

    public void Enable()
    {
        if (_host is null || _engine is null || _commands is null || _dashboard is null)
            throw new InvalidOperationException("Initialize must run before Enable.");
        if (_enabled)
            return;
        _enabled = true;

        _host.Events.Tick += OnTick;
        _commandLease = _host.Commands.Register("bot", _commands.Handle);
        if (_host.ImmediateUi.IsAvailable)
        {
            _drawerLease = _host.ImmediateUi.Register("dashboard", _dashboard.Draw);
        }
        else if (_host.HasUi)
        {
            // Without the overlay, a retail-look status panel still gives
            // start/stop and the four toggles.
            _panelLease = _host.Ui.RegisterPanelContent(
                new PluginPanelDescriptor(BotPanel.WindowId, DisplayName)
                {
                    IconText = "BOT",
                    StartVisible = false,
                },
                BotPanel.Markup,
                new BotPanel(_engine));
        }
        _host.Log.Info("bot ready; /bot for commands");
    }

    public void Disable()
    {
        if (!_enabled)
            return;
        _enabled = false;
        _engine?.Stop();
        if (_host is not null)
            _host.Events.Tick -= OnTick;
        _commandLease?.Dispose();
        _commandLease = null;
        _drawerLease?.Dispose();
        _drawerLease = null;
        _panelLease?.Dispose();
        _panelLease = null;
    }

    private void OnTick(double elapsedSeconds) => _engine?.Tick(elapsedSeconds);

    private static BotProfile? TryLoadDefault(BotStore store, IPluginLogger log)
    {
        try
        {
            return store.LoadProfile(BotProfile.Default.Name);
        }
        catch (Exception error)
        {
            log.Warn($"could not read the default bot profile: {error.Message}");
            return null;
        }
    }
}
