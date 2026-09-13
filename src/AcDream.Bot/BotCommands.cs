using System.Globalization;
using AcDream.Bot.Navigation;
using AcDream.Bot.Profiles;
using AcDream.Plugin.Abstractions;

namespace AcDream.Bot;

/// <summary>The <c>/bot</c> chat command surface.</summary>
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
                    Say("bot started");
                    break;
                case "stop":
                    controller.Stop();
                    Say("bot stopped");
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
                default:
                    Help();
                    break;
            }
        }
        catch (Exception error)
        {
            Say($"bot: {error.Message}");
        }
    }

    private void Status()
    {
        BotEngine engine = controller.Engine;
        BotProfile profile = controller.Profile;
        Say(engine.IsRunning
            ? $"bot running: {engine.ActiveBehaviorName} ({engine.LastReason})"
            : "bot stopped");
        Say($"profile '{profile.Name}': style {profile.Combat.Style}, "
            + $"buffs {(profile.Buffs.Enabled ? "on" : "off")}, "
            + $"combat {(profile.Combat.Enabled ? "on" : "off")}, "
            + $"loot {(profile.Loot.Enabled ? "on" : "off")}, "
            + $"nav {(profile.Navigation.Enabled ? "on" : "off")}");
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
                Say("usage: /bot profile list|load <name>|save [name]|reset");
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
                Say(controller.LoadRoute(args[1])
                    ? $"route '{args[1]}' loaded ({controller.DraftRoute.Waypoints.Count} waypoints)"
                    : $"no route named '{args[1]}'");
                break;
            case "list":
                IReadOnlyList<string> names = controller.Store.RouteNames();
                Say(names.Count == 0 ? "no saved routes" : "routes: " + string.Join(", ", names));
                break;
            default:
                Say("usage: /bot nav add|pause <s>|clear|use|save <name>|load <name>|list");
                break;
        }
    }

    private void Style(string[] args)
    {
        if (args.Length == 0 || !Enum.TryParse(args[0], ignoreCase: true, out CombatStyle style))
        {
            Say("usage: /bot style melee|missile|magic");
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
            Say($"usage: /bot {feature} on|off");
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

    private void Help()
    {
        Say("/bot start|stop|status|rebuff");
        Say("/bot profile list|load <name>|save [name]|reset");
        Say("/bot nav add|pause <s>|clear|use|save <name>|load <name>|list");
        Say("/bot style melee|missile|magic; /bot buffs|combat|loot on|off");
    }

    private void Say(string text) => chat.PostSystemMessage(text);

    private static void RequireArgument(string[] args, int index, string usage)
    {
        if (args.Length <= index)
            throw new ArgumentException("usage: /bot " + usage);
    }

    private static string FormatCoordinate(double value, char positive, char negative) =>
        $"{Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture)}{(value >= 0d ? positive : negative)}";
}
