using AcDream.DrakBot.Behaviors;
using System.Globalization;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot;

/// <summary>The <c>/drakbot</c> chat command surface (<c>/bot</c> is an alias).</summary>
internal sealed class BotCommands(BotController controller, IPluginChat chat)
{
    public void Handle(PluginCommand command)
    {
        string[] words = command.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "status";
        string[] rest = words.Length > 1 ? words[1..] : [];
        controller.Log.Debug($"command: {command.RawText}");
        try
        {
            switch (verb)
            {
                case "start":
                    controller.Start();
                    Say("DrakBot started");
                    break;
                case "stop":
                    controller.Stop();
                    Say("DrakBot stopped");
                    break;
                case "status":
                    Status();
                    break;
                case "log":
                    LogCommand(rest);
                    break;
                case "folder":
                case "files":
                    Folder(rest);
                    break;
                case "profile":
                    Profile(rest);
                    break;
                case "nav":
                    Nav(rest);
                    break;
                case "style":
                    Style(rest);
                    break;
                case "rebuff":
                    controller.Buffs.ForceRebuff();
                    Say("rebuffing everything");
                    break;
                case "spells":
                    if (rest.Length > 0)
                    {
                        string name = string.Join(' ', rest);
                        Say($"{name}: {controller.Buffs.ExplainSpell(name)}");
                    }
                    else
                        Spells();
                    break;
                case "buffs":
                case "combat":
                case "loot":
                    Toggle(verb, rest);
                    break;
                case "los":
                    LineOfSight(rest);
                    break;
                case "meta":
                    MetaCommand(rest);
                    break;
                case "follow":
                    Follow(rest);
                    break;
                case "patrol":
                    if (rest.Length > 0 && rest[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
                    {
                        controller.ClearRoute();
                        Say("patrol stopped");
                        break;
                    }
                    if (rest.Length > 0 && rest[0].Equals("dump", StringComparison.OrdinalIgnoreCase))
                    {
                        Say(controller.TryDumpDungeon(out string dumped) ? dumped : $"cannot dump: {dumped}");
                        break;
                    }
                    Say(controller.TryStartPatrol(out string patrol) ? patrol : $"cannot patrol: {patrol}");
                    break;
                case "goto":
                    GoTo(rest);
                    break;
                case "hazard":
                    Hazard(rest);
                    break;
                case "world":
                    World(rest);
                    break;
                default:
                    if (verb.StartsWith("jump", StringComparison.Ordinal))
                    {
                        Say(controller.TryJump(verb, rest, out string jump) ? jump : $"cannot jump: {jump}");
                        break;
                    }
                    Help();
                    break;
            }
        }
        catch (Exception error)
        {
            Say($"DrakBot: {error.Message}");
        }
    }

    /// <summary>What the character can cast, and where each configured buff stands.</summary>
    private void Spells()
    {
        ISpellCatalog catalog = controller.Engine.Surface.Spells;
        Say($"spellbook: {catalog.KnownSelfBuffs.Count} self buff(s), {catalog.KnownCombatSpells.Count} combat, {catalog.KnownAttackSpells.Count} attack spell(s) known");
        if (controller.Engine.LastBoard is not { } board)
        {
            Say("not in world yet; nothing to check the buffs against");
            return;
        }
        int due = 0;
        foreach (string line in controller.Buffs.Describe(board))
        {
            if (line.EndsWith(": due", StringComparison.Ordinal) || line.Contains(": due,", StringComparison.Ordinal))
                due++;
            Say("  " + line);
        }
        BuffSettings buffs = controller.Profile.Buffs;
        Say(buffs.Enabled
            ? $"buffing on; {due} due"
            : "buffing is off (/drakbot buffs on)");
    }

    private void Status()
    {
        BotEngine engine = controller.Engine;
        BotProfile profile = controller.Profile;
        Say(engine.IsRunning
            ? $"DrakBot running: {engine.ActiveBehaviorName} ({engine.LastReason})"
            : "DrakBot stopped");
        foreach (IBehavior behavior in engine.Behaviors)
        {
            if (behavior is CombatBehavior fights)
                Say($"kills this session: {fights.Kills}");
        }
        Say($"profile '{profile.Name}': style {profile.Combat.Style}, "
            + $"buffs {(profile.Buffs.Enabled ? "on" : "off")}, "
            + $"combat {(profile.Combat.Enabled ? "on" : "off")}, "
            + $"loot {(profile.Loot.Enabled ? "on" : "off")}, "
            + $"nav {(profile.Navigation.Enabled ? "on" : "off")}, "
            + $"los {(profile.Combat.LineOfSight.Enabled ? "on" : "off")}");
        Route? route = controller.Navigation.Route;
        Say(route is null
            ? $"no route loaded; draft has {controller.DraftRoute.Waypoints.Count} waypoints"
            : $"route '{route.Name}': waypoint {controller.Navigation.WaypointIndex + 1}/{route.Waypoints.Count}");
    }

    private void Profile(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
                IReadOnlyList<string> names = controller.Store.ProfileNames();
                Say(names.Count == 0 ? "no saved profiles" : "profiles: " + string.Join(", ", names));
                break;
            case "load":
                RequireArgument(args, 1, "profile load <name>");
                Say(controller.LoadProfile(args[1])
                    ? $"profile '{args[1]}' loaded"
                    : $"no profile named '{args[1]}'");
                break;
            case "save":
                controller.SaveProfile(args.Length > 1 ? args[1] : null);
                Say($"profile '{controller.Profile.Name}' saved");
                break;
            case "reset":
                controller.ResetProfile();
                Say("profile reset to defaults");
                break;
            default:
                Say("usage: /drakbot profile list|load <name>|save [name]|reset");
                break;
        }
    }

    private void Nav(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "add":
                if (!controller.TryAddWaypoint(out PluginNavigationPosition position))
                {
                    Say("position unknown; not in world?");
                    break;
                }
                Say($"waypoint {controller.DraftRoute.Waypoints.Count} added at "
                    + $"{FormatCoordinate(position.NorthSouth, 'N', 'S')}, "
                    + $"{FormatCoordinate(position.EastWest, 'E', 'W')}");
                break;
            case "stalls":
            {
                if (controller.Engine.LastBoard is not { } stallBoard)
                {
                    Say("not in world yet");
                    break;
                }
                uint landblock = stallBoard.Navigation.Position.CellId & 0xFFFF0000u;
                if (args.Length > 1 && args[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    controller.Navigation.Stalls.Clear(landblock);
                    Say($"stall ledger cleared for {landblock >> 16:X4}");
                    break;
                }
                int count = args.Length > 1 && int.TryParse(args[1], out int n) ? Math.Clamp(n, 1, 50) : 10;
                IReadOnlyList<StallSpot> worst = controller.Navigation.Stalls.Worst(landblock, count);
                if (controller.Navigation.Stalls.Current is { } current)
                    Say($"stalled now for {stallBoard.Now - current.Since:0}s at {BotEngine.Describe(current.Where)}");
                if (worst.Count == 0)
                {
                    Say($"no stalls recorded in {landblock >> 16:X4}");
                    break;
                }
                Say($"worst stalls in {landblock >> 16:X4} (time lost, times, longest; step, heading, what freed it, last):");
                foreach (StallSpot spot in worst)
                    Say($"  {spot.Seconds,6:0}s x{spot.Count,-3} {spot.LongestSeconds,4:0}s  {BotEngine.Describe(spot.Position)}  step {spot.Step} h{spot.Heading:0}  {spot.FreedBy}  {spot.LastAt}");
                break;
            }
            case "pause":
                RequireArgument(args, 1, "nav pause <seconds>");
                double seconds = double.Parse(args[1], CultureInfo.InvariantCulture);
                controller.AddPause(seconds);
                Say($"pause of {seconds:0.#}s added");
                break;
            case "clear":
                controller.ClearRoute();
                Say("route cleared");
                break;
            case "use":
                controller.UseDraftRoute();
                Say($"following draft route with {controller.DraftRoute.Waypoints.Count} waypoints");
                break;
            case "save":
                RequireArgument(args, 1, "nav save <name>");
                controller.SaveRoute(args[1]);
                Say($"route '{args[1]}' saved ({controller.DraftRoute.Waypoints.Count} waypoints)");
                break;
            case "load":
                RequireArgument(args, 1, "nav load <name>");
                if (!controller.LoadRoute(args[1], out string? loadWarning))
                {
                    Say($"no route named '{args[1]}'");
                    break;
                }
                if (loadWarning is not null)
                    Say($"route '{args[1]}': {loadWarning}");
                Say($"route '{args[1]}' loaded ({controller.DraftRoute.Waypoints.Count} waypoints)");
                break;
            case "list":
                IReadOnlyList<string> names = controller.Store.RouteNames();
                Say(names.Count == 0 ? "no saved routes" : "routes: " + string.Join(", ", names));
                break;
            case "import":
                RequireArgument(args, 1, "nav import <file.nav>");
                Route imported = controller.ImportNavFile(Rest(args, 1), out string? importWarning);
                if (importWarning is not null)
                    Say($"import: {importWarning}");
                Say($"following '{imported.Name}' ({imported.Waypoints.Count} steps, {imported.Mode?.ToString().ToLowerInvariant() ?? "profile"} mode); nav save <name> keeps it");
                break;
            case "export":
                RequireArgument(args, 1, "nav export <file.nav>");
                controller.ExportNavFile(Rest(args, 1));
                Say($"route written to {Rest(args, 1)}");
                break;
            case "chat":
                RequireArgument(args, 1, "nav chat <text>");
                controller.AddChat(Rest(args, 1));
                Say("chat step added");
                break;
            case "recall":
                RequireArgument(args, 1, "nav recall <spell id>");
                controller.AddRecall(uint.Parse(args[1], CultureInfo.InvariantCulture));
                Say($"recall step added (spell {args[1]})");
                break;
            case "portal":
            case "npc":
                RequireArgument(args, 1, $"nav {sub} <name>");
                WaypointKind kind = sub == "portal" ? WaypointKind.Portal : WaypointKind.Npc;
                if (!controller.TryAddUse(kind, Rest(args, 1), out _))
                {
                    Say("position unknown; not in world?");
                    break;
                }
                Say($"{sub} step '{Rest(args, 1)}' added; stand where the route should use it");
                break;
            default:
                Say("usage: /drakbot nav add|pause <s>|chat <text>|recall <spell>|portal <name>|npc <name>|clear|use|save <name>|load <name>|list|import <file>|export <file>|stalls [n|clear]");
                break;
        }
    }

    private void Style(string[] args)
    {
        if (args.Length == 0 || !Enum.TryParse(args[0], ignoreCase: true, out CombatStyle style))
        {
            Say("usage: /drakbot style melee|missile|magic");
            return;
        }
        controller.Update(profile => profile with { Combat = profile.Combat with { Style = style } });
        Say($"combat style {style}");
    }

    private void Toggle(string feature, string[] args)
    {
        bool? enabled = args.Length == 0
            ? null
            : args[0].ToLowerInvariant() switch
            {
                "on" or "true" or "1" => true,
                "off" or "false" or "0" => false,
                _ => null,
            };
        if (enabled is null)
        {
            Say($"usage: /drakbot {feature} on|off");
            return;
        }
        controller.Update(profile => feature switch
        {
            "buffs" => profile with { Buffs = profile.Buffs with { Enabled = enabled.Value } },
            "combat" => profile with { Combat = profile.Combat with { Enabled = enabled.Value } },
            _ => profile with { Loot = profile.Loot with { Enabled = enabled.Value } },
        });
        Say($"{feature} {(enabled.Value ? "on" : "off")}");
    }

    private void LineOfSight(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        bool debug = sub == "debug";
        string? flag = debug ? (args.Length > 1 ? args[1].ToLowerInvariant() : null) : sub;
        bool? enabled = flag switch
        {
            "on" or "true" or "1" => true,
            "off" or "false" or "0" => false,
            _ => null,
        };
        if (enabled is null)
        {
            Say("usage: /drakbot los on|off | /drakbot los debug on|off");
            return;
        }
        controller.Update(profile => profile with
        {
            Combat = profile.Combat with
            {
                LineOfSight = debug
                    ? profile.Combat.LineOfSight with { ShowDebugSamples = enabled.Value }
                    : profile.Combat.LineOfSight with { Enabled = enabled.Value },
            },
        });
        Say($"line of sight {(debug ? "drawing " : string.Empty)}{(enabled.Value ? "on" : "off")}");
    }

    /// <summary>/drakbot folder [profiles|routes|loot|metas|logs]: says where the bot's files are and opens the folder.</summary>
    private void Folder(string[] args)
    {
        BotFiles files = controller.Files;
        if (files.Directory is not { } root)
        {
            Say("this host keeps no files on disk");
            return;
        }
        string? sub = args.Length > 0
            ? args[0].ToLowerInvariant() switch
            {
                "profiles" or "profile" => BotFiles.ProfilesFolder,
                "routes" or "route" or "nav" or "navs" => BotFiles.RoutesFolder,
                "loot" or "utl" => BotFiles.LootFolder,
                "metas" or "meta" => BotFiles.MetasFolder,
                "logs" or "log" => BotFiles.LogsFolder,
                _ => null,
            }
            : null;
        Say($"DrakBot files: {root}");
        Say("  profiles/ (bot profiles)  routes/ (routes, .nav)  loot/ (.utl)  metas/ (.af, .met)  logs/ (dumps)");
        if (files.VtankDirectory is { } vtank)
            Say($"  also read: {vtank} (the client's shared VTank folder)");
        Say(files.TryOpen(sub, out string opened) ? $"opened {opened}" : $"could not open {opened}");
    }

    private void LogCommand(string[] args)
    {
        BotLog log = controller.Log;
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "quiet":
            case "info":
            case "debug":
            case "trace":
                log.Level = Enum.Parse<BotLogLevel>(sub, ignoreCase: true);
                Say($"log level {log.Level}");
                break;
            case "dump":
            {
                string text = log.Dump();
                string where = controller.Files.WriteLogDump(text);
                Say($"log dumped: {log.Snapshot().Count} lines to {where}");
                break;
            }
            case "clear":
                log.Clear();
                Say("log cleared");
                break;
            case "tail":
            {
                int count = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 10;
                IReadOnlyList<BotLogEntry> entries = log.Snapshot();
                for (int index = Math.Max(0, entries.Count - count); index < entries.Count; index++)
                    Say($"{entries[index].At:HH:mm:ss} {entries[index].Prefix} {entries[index].Text}");
                break;
            }
            default:
                Say($"log level {log.Level}, {log.Snapshot().Count} lines kept; /drakbot log quiet|info|debug|trace | tail [n] | dump | clear");
                break;
        }
    }

    private void Help()
    {
        Say("/drakbot start|stop|status|rebuff");
        Say("/drakbot profile list|load <name>|save [name]|reset");
        Say("/drakbot nav add|pause <s>|chat <text>|recall <spell>|portal <name>|npc <name>|clear|use");
        Say("/drakbot nav save <name>|load <name>|list|import <file.nav>|export <file.nav>");
        Say("/drakbot style melee|missile|magic; /drakbot buffs|combat|loot on|off");
        Say("/drakbot los on|off; /drakbot los debug on|off");
        Say("/drakbot meta load <name>|clear|on|off|state <name>|states|debug on|off|eval <expr>|status");
        Say("/drakbot patrol; /drakbot goto <NS> <EW>; /drakbot hazard add|remove|clear");
        Say("/drakbot follow <name>|leader|off");
        Say("/drakbot log quiet|info|debug|trace | tail [n] | dump | clear");
        Say("/drakbot jump[w|x|z|c|s] [heading] [ms]   (also /ub jump...)");
    }

    private void Follow(string[] args)
    {
        string who = args.Length > 0 ? Rest(args, 0) : string.Empty;
        if (who.Equals("off", StringComparison.OrdinalIgnoreCase) || who.Equals("stop", StringComparison.OrdinalIgnoreCase))
            who = string.Empty;
        controller.Update(p => p with { Navigation = p.Navigation with { Follow = who, Enabled = who.Length > 0 || p.Navigation.Enabled } });
        Say(who.Length == 0 ? "following nobody; the route walks again" : $"following {who}");
    }

    private void GoTo(string[] args)
    {
        RequireArgument(args, 1, "goto <NS> <EW>   (e.g. 41.5N 34.2E)");
        if (!TryCoordinate(args[0], 'N', 'S', out double northSouth) || !TryCoordinate(args[1], 'E', 'W', out double eastWest))
        {
            Say("usage: /drakbot goto <NS> <EW>, e.g. 41.5N 34.2E");
            return;
        }
        Say(controller.TryGoTo(northSouth, eastWest, out string message) ? message : $"cannot go there: {message}");
    }

    /// <summary>
    /// What the client is holding: world objects by kind and name, most
    /// numerous first - for a client that has run for hours and slowed,
    /// to see what has piled up (a corpse the server forgot to remove, a
    /// creature kept after its death).
    /// </summary>
    private void World(string[] args)
    {
        int count = args.Length > 0 && int.TryParse(args[0], out int n) ? Math.Clamp(n, 1, 50) : 15;
        IReadOnlyList<PluginWorldObject> objects = controller.Engine.Surface.Objects.CaptureObjects();
        var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        int owned = 0, positioned = 0;
        foreach (PluginWorldObject o in objects)
        {
            if (o.IsOwned) owned++;
            if (o.HasPosition) positioned++;
            byKind[o.ObjectClass.ToString()] = byKind.GetValueOrDefault(o.ObjectClass.ToString()) + 1;
            string name = $"{o.Name} ({o.ObjectClass})";
            byName[name] = byName.GetValueOrDefault(name) + 1;
        }
        Say($"world: {objects.Count} object(s) known to the client, {owned} owned, {positioned} placed");
        Say("  by kind: " + string.Join(", ", byKind.OrderByDescending(p => p.Value).Select(p => $"{p.Key} {p.Value}")));
        foreach ((string name, int total) in byName.OrderByDescending(p => p.Value).Take(count))
            Say($"  {total,5}  {name}");
    }

    private void Hazard(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "add";
        switch (sub)
        {
            case "add":
                controller.TryMarkHazardAndReroute(out string added);
                Say(added);
                break;
            case "remove":
                controller.TryUnmarkHazard(out string removed);
                Say(removed);
                break;
            case "clear":
                controller.ClearHazards();
                Say("hazards cleared for this dungeon");
                break;
            case "cross":
            {
                bool? cross = args.Length > 1 ? args[1].ToLowerInvariant() switch { "on" or "true" or "1" => true, "off" or "false" or "0" => false, _ => null } : null;
                if (cross is null)
                {
                    Say($"hazards are {(controller.Profile.Navigation.CrossHazards ? "crossed when they are the only way" : "never entered")}; /drakbot hazard cross on|off");
                    break;
                }
                controller.Update(profile => profile with { Navigation = profile.Navigation with { CrossHazards = cross.Value } });
                controller.TryStartPatrol(out string rebuilt);
                Say($"hazards are now {(cross.Value ? "crossed when they are the only way" : "never entered")}; {rebuilt}");
                break;
            }
            default:
                Say("usage: /drakbot hazard add|remove|clear|cross on|off   (add/remove mark the cell you stand in)");
                break;
        }
    }

    /// <summary>Reads a coordinate the way the game prints it: 41.5N, 34.2E, or a bare signed number.</summary>
    private static bool TryCoordinate(string text, char positive, char negative, out double value)
    {
        string trimmed = text.Trim();
        double sign = 1d;
        if (trimmed.Length > 0 && char.ToUpperInvariant(trimmed[^1]) == char.ToUpperInvariant(negative))
        {
            sign = -1d;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.Length > 0 && char.ToUpperInvariant(trimmed[^1]) == char.ToUpperInvariant(positive))
        {
            trimmed = trimmed[..^1];
        }
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude))
        {
            value = 0d;
            return false;
        }
        value = sign * magnitude;
        return true;
    }

    private void MetaCommand(string[] args)
    {
        MetaEngine? meta = controller.Meta;
        if (meta is null)
        {
            Say("no meta engine on this host");
            return;
        }
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "load":
                RequireArgument(args, 1, "meta load <name>");
                meta.LoadByName(Rest(args, 1));
                break;
            case "clear":
                meta.Clear();
                controller.Update(p => p with { Meta = p.Meta with { Name = string.Empty } });
                Say("meta cleared");
                break;
            case "on":
            case "off":
                meta.Enabled = sub == "on";
                Say($"meta {sub}");
                break;
            case "state":
                RequireArgument(args, 1, "meta state <name>");
                meta.SetState(Rest(args, 1));
                Say($"meta state -> {meta.CurrentState}");
                break;
            case "states":
                IReadOnlyList<string> states = meta.StateNames();
                Say(states.Count == 0 ? "no meta loaded" : "states: " + string.Join(", ", states));
                break;
            case "debug":
                RequireArgument(args, 1, "meta debug on|off");
                meta.Debug = args[1].Equals("on", StringComparison.OrdinalIgnoreCase);
                Say($"meta debug {(meta.Debug ? "on" : "off")}");
                break;
            case "eval":
                RequireArgument(args, 1, "meta eval <expression>");
                Say(meta.Expressions.Evaluate(Rest(args, 1)));
                break;
            case "status":
                Say(meta.Rules.Count == 0
                    ? $"meta: none loaded ({(meta.Enabled ? "on" : "off")})"
                    : $"meta '{meta.MetaName}': {(meta.Enabled ? "on" : "off")}, state {meta.CurrentState} for {meta.SecondsInState:0}s, "
                      + $"{meta.Rules.Count} rules, stack {meta.StackDepth}"
                      + (meta.WatchdogActive ? ", watchdog armed" : string.Empty));
                if (meta.LastFired.Length > 0)
                    Say($"last fired: {meta.LastFired}");
                if (meta.LastError.Length > 0)
                    Say($"last error: {meta.LastError}");
                break;
            default:
                Say("usage: /drakbot meta load <name>|clear|on|off|state <name>|states|debug on|off|eval <expr>|status");
                break;
        }
    }

    private void Say(string text) => chat.PostSystemMessage(text);

    private static void RequireArgument(string[] args, int index, string usage)
    {
        if (args.Length <= index)
            throw new ArgumentException("usage: /drakbot " + usage);
    }

    /// <summary>The arguments from <paramref name="index"/> on, joined back into the text the user typed.</summary>
    private static string Rest(string[] args, int index) =>
        string.Join(' ', args.Skip(index));

    private static string FormatCoordinate(double value, char positive, char negative) =>
        $"{Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture)}{(value >= 0d ? positive : negative)}";
}
