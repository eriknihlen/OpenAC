using System.Globalization;
using System.Numerics;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The navigation window as RynthAi lays it out: the active route and
/// what the walk is doing, a Start/Stop button, route type and where new
/// steps go (end, above or below the selected one), buttons that record
/// a waypoint, portal, NPC, vendor, recall, pause or chat step where the
/// character stands, the step list with delete buttons and the current
/// step marked, and a load/save bar over the saved routes and the VTank
/// <c>.nav</c> files. Edits to the route being walked take effect at once.
/// </summary>
public sealed class NavigationWindow(BotController controller, IAutomationSurface surface)
{
    private static readonly string[] RouteTypes = ["Profile", "Once", "Loop", "Ping-pong"];
    private static readonly string[] InsertModes = ["End", "Above", "Below"];
    private static readonly Vector4 ColYellow = new(1f, 1f, 0f, 1f);
    private static readonly Vector4 ColGreen = new(0.25f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 ColAmber = new(0.91f, 0.70f, 0.20f, 1f);
    private static readonly Vector4 ColAccent = new(0.15f, 0.85f, 0.90f, 1f);

    private static readonly (uint Id, string Name)[] Recalls =
    [
        (48u, "Primary Portal Recall"),
        (2647u, "Secondary Portal Recall"),
        (2645u, "Portal Recall"),
        (1635u, "Lifestone Recall"),
        (1636u, "Lifestone Sending"),
        (2931u, "Recall Aphus Lassel"),
        (2023u, "Recall the Sanctuary"),
        (2041u, "Recall to the Singularity Caul"),
        (2358u, "Glenden Wood Recall"),
        (2813u, "Aerlinthe Recall"),
        (2941u, "Mount Lethe Recall"),
        (2943u, "Ulgrim's Recall"),
        (3865u, "Bur Recall"),
        (3929u, "Paradox-touched Olthoi Infested Area Recall"),
        (3930u, "Call of the Mhoire Forge"),
        (4084u, "Colosseum Recall"),
        (4198u, "Return to the Keep"),
        (4213u, "Gear Knight Invasion Area Camp Recall"),
        (4907u, "Lost City of Neftet Recall"),
        (4908u, "Rynthid Recall"),
        (4909u, "Viridian Rise Recall"),
        (5175u, "Viridian Rise Great Hall Recall"),
        (5330u, "Celestial Hand Stronghold Recall"),
        (5541u, "Eldrytch Web Stronghold Recall"),
        (6150u, "Radiant Blood Stronghold Recall"),
        (6321u, "Facility Hub Recall"),
        (6322u, "Candeth Keep Recall"),
    ];

    private bool _open;
    private int _insertMode;
    private int _selected = -1;
    private string _useName = string.Empty;
    private string _chatText = string.Empty;
    private float _pauseSeconds = 2f;
    private string _routeName = string.Empty;
    private IReadOnlyList<string> _savedRoutes = [];
    private IReadOnlyList<string> _navFiles = [];
    private double _refreshedAt = double.NegativeInfinity;
    private string _status = string.Empty;
    private double _statusAt = double.NegativeInfinity;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        ImGui.SetNextWindowSize(new Vector2(440f, 560f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Navigation##drakbot", ref _open))
        {
            ImGui.End();
            return;
        }
        Refresh();
        Route draft = controller.DraftRoute;
        NavigationSettings nav = controller.Profile.Navigation;
        Route? walked = controller.Navigation.Route;

        ImGui.TextColored(ColYellow, $"Active Nav: {(walked is null ? "None" : walked.Name)}" + (draft.Name != "draft" && walked?.Name != draft.Name ? $"   (editing: {draft.Name})" : string.Empty));
        string status = StatusLine(walked);
        if (status.Length > 0)
            ImGui.TextColored(controller.Engine.LastReason.Contains("stuck", StringComparison.OrdinalIgnoreCase) ? ColAmber : ColGreen, status);

        ImGui.Spacing();
        bool navigating = controller.Engine.IsRunning && nav.Enabled;
        if (navigating)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.50f, 0.10f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.70f, 0.15f, 0.15f, 1f));
            if (ImGui.Button("Stop Navigation", new Vector2(-1f, 28f)))
                controller.Update(p => p with { Navigation = p.Navigation with { Enabled = false } });
            ImGui.PopStyleColor(2);
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.10f, 0.38f, 0.10f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.15f, 0.55f, 0.15f, 1f));
            if (ImGui.Button("Start Navigation", new Vector2(-1f, 28f)))
            {
                if (walked is null && !draft.IsEmpty)
                    controller.UseDraftRoute();
                controller.Update(p => p with { Navigation = p.Navigation with { Enabled = true } });
                controller.Engine.Start();
            }
            ImGui.PopStyleColor(2);
        }
        ImGui.Separator();

        int routeType = draft.Mode switch
        {
            RouteMode.Once => 1,
            RouteMode.Loop => 2,
            RouteMode.PingPong => 3,
            _ => 0,
        };
        ImGui.SetNextItemWidth(110f);
        if (ImGui.Combo("Route Type", ref routeType, RouteTypes, RouteTypes.Length))
        {
            controller.SetDraftMode(routeType switch
            {
                1 => RouteMode.Once,
                2 => RouteMode.Loop,
                3 => RouteMode.PingPong,
                _ => null,
            });
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f);
        ImGui.Combo("Insert", ref _insertMode, InsertModes, InsertModes.Length);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Where a new step goes: the end of the route, or above/below the selected step");
        ImGui.Spacing();

        if (ImGui.Button("Add Waypoint", new Vector2(100f, 25f)))
            Record(WaypointKind.Point);
        ImGui.SameLine();
        if (ImGui.Button("Add Portal", new Vector2(100f, 25f)))
            ImGui.OpenPopup("AddPortal");
        ImGui.SameLine();
        if (ImGui.Button("Add Recall", new Vector2(100f, 25f)))
            ImGui.OpenPopup("AddRecall");
        ImGui.SameLine();
        if (ImGui.Button("Add NPC", new Vector2(100f, 25f)))
            ImGui.OpenPopup("AddNpc");

        if (ImGui.Button("Add Pause", new Vector2(100f, 25f)))
            ImGui.OpenPopup("AddPause");
        ImGui.SameLine();
        if (ImGui.Button("Add Chat", new Vector2(100f, 25f)))
            ImGui.OpenPopup("AddChat");
        ImGui.SameLine();
        if (ImGui.Button("Clear Route", new Vector2(100f, 25f)))
        {
            controller.ClearRoute();
            _selected = -1;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(draft.IsEmpty);
        if (ImGui.Button(walked?.Name == draft.Name ? "Restart Route" : "Follow Route", new Vector2(100f, 25f)))
            controller.UseDraftRoute();
        ImGui.EndDisabled();

        DrawPopups();

        if (ImGui.BeginListBox("##steps", new Vector2(-1f, 220f)))
        {
            for (int i = 0; i < draft.Waypoints.Count; i++)
            {
                Waypoint waypoint = draft.Waypoints[i];
                ImGui.PushID(i);
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                if (ImGui.Button("X", new Vector2(20f, 20f)))
                {
                    controller.RemoveWaypoint(i);
                    if (_selected == i)
                        _selected = -1;
                    else if (_selected > i)
                        _selected--;
                    ImGui.PopStyleColor();
                    ImGui.PopID();
                    break;
                }
                ImGui.PopStyleColor();
                ImGui.SameLine();
                bool current = walked is not null && walked.Name == draft.Name && controller.Navigation.WaypointIndex == i;
                string prefix = current ? "==>" : "   ";
                if (current)
                    ImGui.PushStyleColor(ImGuiCol.Text, ColAccent);
                if (ImGui.Selectable($"{prefix} [{i}] {Describe(waypoint)}", _selected == i))
                    _selected = i;
                if (current)
                    ImGui.PopStyleColor();
                ImGui.PopID();
            }
            ImGui.EndListBox();
        }

        ImGui.Separator();
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 170f);
        if (ImGui.BeginCombo("##routes", "Load a route..."))
        {
            foreach (string name in _savedRoutes)
            {
                if (ImGui.Selectable($"{name} (saved)"))
                    Load(name);
            }
            foreach (string file in _navFiles)
            {
                if (ImGui.Selectable(file))
                    Load(file[..^4]);
            }
            if (_savedRoutes.Count == 0 && _navFiles.Count == 0)
                ImGui.TextDisabled("no saved routes or .nav files");
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputTextWithHint("##routename", "name", ref _routeName, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(draft.IsEmpty || string.IsNullOrWhiteSpace(_routeName));
        if (ImGui.Button("Save", new Vector2(55f, 0f)))
        {
            controller.SaveRoute(_routeName.Trim());
            _refreshedAt = double.NegativeInfinity;
            Status($"saved route {_routeName.Trim()}");
        }
        ImGui.EndDisabled();
        if (_status.Length > 0 && ImGui.GetTime() - _statusAt < 5d)
            ImGui.TextColored(ColGreen, _status);
        else
            ImGui.TextDisabled($"{draft.Waypoints.Count} steps; /drakbot nav import <file.nav> reads a VTank route from disk");
        ImGui.End();
    }

    private void DrawPopups()
    {
        if (ImGui.BeginPopup("AddPortal"))
        {
            ImGui.Text("Portal name (part of it is enough):");
            ImGui.SetNextItemWidth(240f);
            bool submit = ImGui.InputText("##portalname", ref _useName, 64, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.TextDisabled("Nearby:");
            foreach (PluginWorldObject candidate in Nearby(PluginObjectClass.Portal))
            {
                if (ImGui.Selectable(candidate.Name))
                {
                    _useName = candidate.Name;
                    submit = true;
                }
            }
            if ((ImGui.Button("Add", new Vector2(100f, 0f)) || submit) && _useName.Trim().Length > 0)
            {
                Record(WaypointKind.Portal, _useName.Trim());
                _useName = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("AddNpc"))
        {
            ImGui.Text("NPC or vendor name:");
            ImGui.SetNextItemWidth(240f);
            ImGui.InputText("##npcname", ref _useName, 64);
            ImGui.TextDisabled("Nearby:");
            foreach (PluginWorldObject candidate in Nearby(PluginObjectClass.Npc, PluginObjectClass.Vendor))
            {
                if (ImGui.Selectable($"{candidate.Name}{(candidate.ObjectClass == PluginObjectClass.Vendor ? " (vendor)" : string.Empty)}"))
                    _useName = candidate.Name;
            }
            if (ImGui.Button("Add NPC", new Vector2(100f, 0f)) && _useName.Trim().Length > 0)
            {
                Record(WaypointKind.Npc, _useName.Trim());
                _useName = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Add Vendor (sell)", new Vector2(130f, 0f)) && _useName.Trim().Length > 0)
            {
                Record(WaypointKind.Vendor, _useName.Trim());
                _useName = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("AddRecall"))
        {
            foreach ((uint id, string name) in Recalls)
            {
                if (ImGui.Selectable($"{name} ({id})"))
                {
                    Insert(new Waypoint(WaypointKind.Recall, 0d, 0d) { SpellId = id });
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("AddPause"))
        {
            ImGui.SetNextItemWidth(100f);
            ImGui.InputFloat("seconds", ref _pauseSeconds, 0f, 0f, "%.1f");
            if (ImGui.Button("Add", new Vector2(100f, 0f)))
            {
                Insert(new Waypoint(WaypointKind.Pause, 0d, 0d) { Seconds = Math.Max(0.1f, _pauseSeconds) });
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopup("AddChat"))
        {
            ImGui.SetNextItemWidth(300f);
            bool submit = ImGui.InputTextWithHint("##chat", "/say hello, or a /command", ref _chatText, 256, ImGuiInputTextFlags.EnterReturnsTrue);
            if ((ImGui.Button("Add", new Vector2(100f, 0f)) || submit) && _chatText.Trim().Length > 0)
            {
                Insert(new Waypoint(WaypointKind.Chat, 0d, 0d) { Text = _chatText.Trim() });
                _chatText = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
    }

    private void Record(WaypointKind kind, string targetName = "")
    {
        if (!controller.TryMakeWaypoint(kind, out Waypoint waypoint))
        {
            Status("the character's position is not known yet");
            return;
        }
        if (targetName.Length > 0)
            waypoint = waypoint with { TargetName = targetName };
        Insert(waypoint);
    }

    private void Insert(Waypoint waypoint)
    {
        int count = controller.DraftRoute.Waypoints.Count;
        int index = _insertMode switch
        {
            1 when _selected >= 0 && _selected < count => _selected,
            2 when _selected >= 0 && _selected < count => _selected + 1,
            _ => count,
        };
        controller.InsertWaypoint(index, waypoint);
        if (_insertMode == 2 && _selected >= 0)
            _selected++;
    }

    private void Load(string name)
    {
        if (controller.LoadRouteByName(name))
        {
            _routeName = name;
            _selected = -1;
            Status($"loaded {name}: {controller.DraftRoute.Waypoints.Count} steps");
        }
        else
        {
            Status($"could not load {name}");
        }
    }

    private void Refresh()
    {
        double now = ImGui.GetTime();
        if (now - _refreshedAt < 5d)
            return;
        _refreshedAt = now;
        _savedRoutes = controller.Store.RouteNames();
        _navFiles = controller.NavFileNames();
    }

    private string StatusLine(Route? walked)
    {
        if (walked is null)
            return string.Empty;
        string action = controller.Navigation.ActionStatus;
        if (action.Length > 0)
            return action;
        int index = controller.Navigation.WaypointIndex;
        if (index < 0)
            return "finished";
        string reason = controller.Engine.ActiveBehaviorName == "navigation" ? controller.Engine.LastReason : "waiting";
        return $"step {index + 1}/{walked.Waypoints.Count} - {reason}";
    }

    private IEnumerable<PluginWorldObject> Nearby(params PluginObjectClass[] classes)
    {
        PluginNavigationSnapshot snapshot = surface.Navigation.Snapshot;
        var found = new List<(double Distance, PluginWorldObject Object)>();
        foreach (PluginWorldObject candidate in surface.Objects.CaptureObjects())
        {
            if (candidate.IsOwned || Array.IndexOf(classes, candidate.ObjectClass) < 0 || !candidate.HasPosition)
                continue;
            double distance = snapshot.IsAvailable ? candidate.Position.HorizontalDistanceMeters(snapshot.Position) : 0d;
            if (distance <= 100d)
                found.Add((distance, candidate));
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found.Take(12).Select(entry => entry.Object);
    }

    private void Status(string text)
    {
        _status = text;
        _statusAt = ImGui.GetTime();
    }

    private static string Describe(Waypoint waypoint) => waypoint.Kind switch
    {
        WaypointKind.Pause => $"Pause {waypoint.Seconds:0.#} s",
        WaypointKind.Chat => $"Chat: {waypoint.Text}",
        WaypointKind.Recall => $"Recall: {RecallName(waypoint.SpellId)}",
        WaypointKind.Portal => $"Portal: {waypoint.TargetName}",
        WaypointKind.Npc => $"NPC: {waypoint.TargetName}",
        WaypointKind.Vendor => $"Vendor: {waypoint.TargetName}",
        WaypointKind.Point => $"{Coordinate(waypoint.NorthSouth, 'N', 'S')}, {Coordinate(waypoint.EastWest, 'E', 'W')}",
        _ => $"{waypoint.Kind} {Coordinate(waypoint.NorthSouth, 'N', 'S')}, {Coordinate(waypoint.EastWest, 'E', 'W')}",
    };

    private static string RecallName(uint spellId)
    {
        foreach ((uint id, string name) in Recalls)
        {
            if (id == spellId)
                return name;
        }
        return $"spell {spellId}";
    }

    private static string Coordinate(double value, char positive, char negative) =>
        $"{Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture)}{(value >= 0d ? positive : negative)}";
}
