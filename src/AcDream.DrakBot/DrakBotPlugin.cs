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
        var navigation = new NavigationBehavior(() => engine.Profile.Navigation, new StallLedger(host.Storage));
        Func<string> wand = () => engine.Profile.Combat.Wand;
        var buffs = new SelfBuffBehavior(spells, Casts(), () => engine.Profile.Buffs, surface, wand);
        var lineOfSight = new LineOfSightService(
            surface.Projectiles,
            surface.MovementProbe,
            clock,
            () => engine.Profile.Combat.LineOfSight);
        var files = new BotFiles(host.Storage, host.VtankProfiles);
        VTankLootProfile? LoadUtl(string name)
        {
            string? text = files.ReadUtl(name);
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
            new VitalRechargeBehavior(spells, Casts(), () => engine.Profile.Vitals, surface.Fellowship, wand),
            buffs,
            new PetBehavior(() => engine.Profile.Pets),
            new ManaStoneBehavior(() => engine.Profile.ManaStones),
            new CombatBehavior(surface, spells, Casts(), lineOfSight, () => engine.Profile.Combat),
            new DoorBehavior(
                () => engine.Profile.Doors,
                () => engine.Profile.Navigation.Enabled && navigation.Route is not null,
                surface.Navigation.CaptureObjects,
                doorId => DoorBlocks(surface, doorId)),
            loot,
            salvage,
            navigation,
        ];
        engine = new BotEngine(surface, log, behaviors, clock);
        _engine = engine;

        var store = new BotStore(host.Storage);
        BotProfile? saved = store.IsAvailable ? TryLoadLast(store, log) : null;
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
        _controller.ChatAfter = surface.Chat.CaptureMessages;
        _controller.ApplyProfileMeta();
        _controller.ApplyProfileRoute();
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

        ApplySessionSettings(_host.SessionSettings);
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

    /// <summary>Key in a host's per-session settings: the profile to load on enable.</summary>
    public const string ProfileSetting = "profile";

    /// <summary>Key in a host's per-session settings: "true"/"false", overrides the profile's patrol-on-login switch for this session.</summary>
    public const string PatrolOnLoginSetting = "patrolOnLogin";

    /// <summary>
    /// A host's per-session settings for the bot (the headless
    /// configuration's <c>pluginSettings["acdream.drakbot"]</c>). The
    /// profile is loaded like <c>/drakbot profile load</c>; the patrol
    /// switch is set on the live profile without being written back, so a
    /// session's override does not become the saved profile's answer.
    /// </summary>
    private void ApplySessionSettings(IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count == 0 || _controller is null || _engine is null || _log is null)
            return;
        if (settings.TryGetValue(ProfileSetting, out string? profile)
            && !string.IsNullOrWhiteSpace(profile))
        {
            string name = profile.Trim();
            if (_controller.LoadProfile(name))
                _log.Info($"session settings: profile '{name}' loaded");
            else
                _log.Warn($"session settings: there is no profile '{name}'");
        }
        if (settings.TryGetValue(PatrolOnLoginSetting, out string? patrol)
            && !string.IsNullOrWhiteSpace(patrol))
        {
            if (bool.TryParse(patrol.Trim(), out bool patrolOnLogin))
            {
                _engine.Profile = _engine.Profile with
                {
                    Navigation = _engine.Profile.Navigation with { PatrolOnLogin = patrolOnLogin },
                };
                _log.Info($"session settings: patrol on login {(patrolOnLogin ? "on" : "off")}");
            }
            else
            {
                _log.Warn($"session settings: patrolOnLogin wants true or false, not '{patrol}'");
            }
        }
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
        _controller?.FlushProfile();
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

    /// <summary>
    /// Whether a walk at a door runs into it: true for a closed door, false
    /// for one the body passes through, null when there is no probe or no
    /// position to walk from.
    /// </summary>
    private static bool? DoorBlocks(IAutomationSurface surface, uint doorId)
    {
        if (!surface.MovementProbe.IsAvailable
            || !surface.Navigation.Snapshot.IsAvailable
            || !surface.Navigation.TryGetObject(doorId, out PluginNavigationObject door))
        {
            return null;
        }
        PluginNavigationPosition here = surface.Navigation.Snapshot.Position;
        float distance = (float)here.HorizontalDistanceMeters(door.Position);
        if (distance > 12f)
            return null;
        float heading = Navigation.RouteFollower.HeadingTo(here, door.Position);
        PluginWalkProbeResult result = surface.MovementProbe.ProbeWalk(new PluginWalkProbeRequest(heading, distance + 1.5f)
        {
            StepDistance = 0.5f,
            MaximumCollisionChecks = 40,
        });
        if (result.Status == PluginWalkProbeStatus.Blocked)
            return result.BlockingObjectId == doorId || result.BlockingObjectId == 0u ? true : null;
        return result.Status == PluginWalkProbeStatus.Clear ? false : null;
    }

    private void OnTick(double elapsedSeconds)
    {
        _engine?.Tick(elapsedSeconds);
        if (_engine is not null)
            _controller?.Tick(_engine.Clock.Now);
    }

    /// <summary>The profile in use last time, else the default one.</summary>
    private static BotProfile? TryLoadLast(BotStore store, IPluginLogger log)
    {
        string name = store.LastProfileName ?? BotProfile.Default.Name;
        try
        {
            return store.LoadProfile(name) ?? (name == BotProfile.Default.Name ? null : store.LoadProfile(BotProfile.Default.Name));
        }
        catch (Exception error)
        {
            log.Warn($"could not read the bot profile '{name}': {error.Message}");
            return null;
        }
    }
}

/// <summary>
/// Builds the bot's tool windows for a graphical host and returns the
/// per-frame draw callback the host's immediate-mode overlay calls.
/// </summary>
public delegate Action DrakBotWindowsFactory(BotController controller, IAutomationSurface surface, IImmediateUiHost ui);
