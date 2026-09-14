using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Loot.Utl;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>
/// DrakBot as the host sees it: a gameplay plugin that ticks the engine,
/// owns the <c>/drakbot</c> command (and its <c>/bot</c> alias) and its
/// windows. Nothing here touches client internals; every action goes
/// through <see cref="IAutomationSurface"/>.
/// </summary>
/// <remarks>
/// The tool windows are Dear ImGui and live in <c>AcDream.DrakBot.Ui</c>
/// so this assembly carries no presentation library; a graphical host
/// passes a <see cref="DrakBotWindowsFactory"/> and a headless one passes
/// nothing. Without windows the retail-look status panel stands in when the
/// host has a UI at all.
/// </remarks>
public sealed class DrakBotPlugin(DrakBotWindowsFactory? windows = null) : IAcDreamPlugin
{
    public const string Id = "acdream.drakbot";
    public const string DisplayName = "DrakBot";
    public const string Version = "0.1.0";

    private IPluginHost? _host;
    private BotEngine? _engine;
    private BotController? _controller;
    private BotCommands? _commands;
    private Action? _drawWindows;
    private BotLog? _log;
    private IDisposable? _commandLease;
    private IDisposable? _aliasLease;
    private IDisposable? _vtLease;
    private IDisposable? _ubLease;
    private IDisposable? _mtLease;
    private IDisposable? _raLease;
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
        // Everything the bot says goes through its own log: a ring for the
        // Log window and dumps, and on to the host's log (the client's file).
        var log = new BotLog(host.Log, () => clock.Now);
        _log = log;
        var cooldowns = new CastCooldowns(clock);
        BotEngine engine = null!;
        var spells = new SpellSelector(
            surface.Spells,
            surface.Magic,
            cooldowns.IsOnCooldown,
            new SpellTierGate(surface.Character, () => engine.Profile.SpellTiers));
        CastTracker Casts() => new(surface.Magic, clock, cooldowns);
        // Behaviors read the live profile through the engine, which does not
        // exist until they do; the closures resolve it lazily.
        var navigation = new NavigationBehavior(() => engine.Profile.Navigation);
        var buffs = new SelfBuffBehavior(spells, Casts(), () => engine.Profile.Buffs, surface);
        var lineOfSight = new LineOfSightService(
            surface.Projectiles,
            surface.MovementProbe,
            clock,
            () => engine.Profile.Combat.LineOfSight);
        VTankLootProfile? LoadUtl(string name)
        {
            string? text = host.VtankProfiles.ReadText(BotStore.SanitizeName(name) + ".utl");
            return text is null ? null : VTankLootParser.LoadFromText(text);
        }
        LootBehavior loot = null!;
        var salvage = new SalvageBehavior(
            () => engine.Profile.Salvage,
            () => loot.UtlProfile(engine.Profile.Loot)?.SalvageCombine,
            surface.Items.CaptureOwnedItems);
        loot = new LootBehavior(
            () => engine.Profile.Loot,
            LoadUtl,
            () => engine.Profile.ManaStones,
            salvage.Enqueue);
        navigation.ItemsToSell = () => loot.ItemsToSell(surface, engine.Profile.Loot);
        IBehavior[] behaviors =
        [
            new VitalRechargeBehavior(spells, Casts(), () => engine.Profile.Vitals, surface.Fellowship),
            buffs,
            new PetBehavior(() => engine.Profile.Pets),
            new ManaStoneBehavior(() => engine.Profile.ManaStones),
            new CombatBehavior(surface, spells, Casts(), lineOfSight, () => engine.Profile.Combat),
            new DoorBehavior(
                () => engine.Profile.Doors,
                () => engine.Profile.Navigation.Enabled && navigation.Route is not null,
                surface.Navigation.CaptureObjects),
            loot,
            salvage,
            navigation,
        ];
        engine = new BotEngine(surface, log, behaviors, clock);
        _engine = engine;

        var store = new BotStore(host.Storage);
        BotProfile? saved = store.IsAvailable ? TryLoadDefault(store, log) : null;
        if (saved is not null)
            _engine.Profile = saved;

        _controller = new BotController(
            _engine,
            store,
            navigation,
            buffs,
            () => surface.Navigation.Snapshot,
            host.VtankProfiles,
            surface.Dungeon,
            new DungeonHazards(host.Storage),
            log);
        _commands = new BotCommands(_controller, surface.Chat);
        BotCommands commands = _commands;
        _controller.CommandHandler = text =>
        {
            commands.Handle(new PluginCommand("drakbot", text, "/drakbot " + text));
            return true;
        };
        // The meta sees the world through the automation surface and keeps
        // its variables in the plugin's storage; it reads its own switches
        // from the live profile.
        BotController controller = _controller;
        var world = new MetaWorld(surface, host.Storage, log, host.Selection, () => clock.Now);
        _controller.Meta = new MetaEngine(
            world,
            _controller,
            clock,
            log,
            () => controller.Profile.Meta,
            change => controller.Update(p => p with { Meta = change(p.Meta) }));
        _controller.ObjectScan = surface.Objects.CaptureObjects;
        _controller.Meta.Utility = new UtilityCommands(
            world,
            _controller.Meta,
            surface,
            LoadUtl,
            verb =>
            {
                if (_commands is null)
                    return false;
                _commands.Handle(new PluginCommand("drakbot", verb, "/drakbot " + verb));
                return true;
            });
        _controller.ApplyProfileMeta();
        _drawWindows = windows?.Invoke(_controller, surface, host.ImmediateUi);
    }

    public void Enable()
    {
        if (_host is null || _engine is null || _commands is null)
            throw new InvalidOperationException("Initialize must run before Enable.");
        if (_enabled)
            return;
        _enabled = true;

        _host.Events.Tick += OnTick;
        _commandLease = _host.Commands.Register("drakbot", _commands.Handle);
        _aliasLease = _host.Commands.Register("bot", _commands.Handle);
        // VTank metas and habits speak /vt; the meta engine translates it.
        _vtLease = _host.Commands.Register("vt", command =>
            _controller?.Meta?.SendCommand(command.RawText));
        // UtilityBelt metas jump with /ub jump...; the rest of /ub and /mt
        // are the MagTools verbs (use, cast, opt, fellow, ...).
        _ubLease = _host.Commands.Register("ub", command => HandleUtility("/ub", command));
        _mtLease = _host.Commands.Register("mt", command => HandleUtility("/mt", command));
        _raLease = _host.Commands.Register("ra", command => HandleUtility("/ra", command));
        if (_drawWindows is not null && _host.ImmediateUi.IsAvailable)
        {
            _drawerLease = _host.ImmediateUi.Register("dashboard", _drawWindows);
        }
        else if (_host.HasUi)
        {
            // Without the overlay, a retail-look status panel still gives
            // start/stop and the four toggles.
            _panelLease = _host.Ui.RegisterPanelContent(
                new PluginPanelDescriptor(BotPanel.WindowId, DisplayName)
                {
                    IconText = "DRAK",
                    StartVisible = false,
                },
                BotPanel.Markup,
                new BotPanel(_engine));
        }
        _host.Log.Info("DrakBot ready; /drakbot for commands");
    }

    private void HandleUtility(string prefix, PluginCommand command)
    {
        string[] words = command.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length > 0 && words[0].StartsWith("jump", StringComparison.OrdinalIgnoreCase))
        {
            _commands?.Handle(new PluginCommand("drakbot", command.Arguments, "/drakbot " + command.Arguments));
            return;
        }
        if (_controller?.Meta?.Utility?.TryHandle(prefix + " " + command.Arguments) != true)
            _host?.Automation.Chat.PostSystemMessage($"DrakBot does not know {prefix} {words.FirstOrDefault()}; see /drakbot");
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
        _aliasLease?.Dispose();
        _aliasLease = null;
        _vtLease?.Dispose();
        _vtLease = null;
        _ubLease?.Dispose();
        _ubLease = null;
        _mtLease?.Dispose();
        _mtLease = null;
        _raLease?.Dispose();
        _raLease = null;
        _drawerLease?.Dispose();
        _drawerLease = null;
        _panelLease?.Dispose();
        _panelLease = null;
    }

    private void OnTick(double elapsedSeconds)
    {
        _engine?.Tick(elapsedSeconds);
        if (_engine is not null)
            _controller?.Tick(_engine.Clock.Now);
    }

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

/// <summary>
/// Builds the bot's tool windows for a graphical host and returns the
/// per-frame draw callback the host's immediate-mode overlay calls.
/// </summary>
public delegate Action DrakBotWindowsFactory(BotController controller, IAutomationSurface surface, IImmediateUiHost ui);
