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
                default:
                    Help();
                    break;
            }
        }
        catch (Exception error)
        {
            Say($"DrakBot: {error.Message}");
        }
    }

    private void Status()
    {
        BotEngine engine = controller.Engine;
        BotProfile profile = controller.Profile;
        Say(engine.IsRunning
            ? $"DrakBot running: {engine.ActiveBehaviorName} ({engine.LastReason})"
            : "DrakBot stopped");
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
                Say("usage: /drakbot nav add|pause <s>|chat <text>|recall <spell>|portal <name>|npc <name>|clear|use|save <name>|load <name>|list|import <file>|export <file>");
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

    private void Help()
    {
        Say("/drakbot start|stop|status|rebuff");
        Say("/drakbot profile list|load <name>|save [name]|reset");
        Say("/drakbot nav add|pause <s>|chat <text>|recall <spell>|portal <name>|npc <name>|clear|use");
        Say("/drakbot nav save <name>|load <name>|list|import <file.nav>|export <file.nav>");
        Say("/drakbot style melee|missile|magic; /drakbot buffs|combat|loot on|off");
        Say("/drakbot los on|off; /drakbot los debug on|off");
        Say("/drakbot meta load <name>|clear|on|off|state <name>|states|debug on|off|eval <expr>|status");
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
