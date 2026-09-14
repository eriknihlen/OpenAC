using System.Numerics;
using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Meta;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;
using ImGuiNET;
using static AcDream.DrakBot.Ui.DashboardDrawing;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The main window, laid out as RynthAi's dashboard: a title bar with
/// lock, opacity, minimise and close; RUNNING/STOPPED with its status
/// light, the meta state and the bot's activity on the left, the profile,
/// nav, loot and meta pickers on the right; the combat panel with the
/// square toggles (combat, buffing, navigation, looting), the MACRO
/// toggle and force-rebuff beside the target's segmented health bar and
/// the player's vitals; and the launcher grid underneath. Minimised, only
/// the combat panel with a small Running/Stopped button remains.
/// </summary>
public sealed class BotDashboard
{
    private const string Version = "v0.1";

    private readonly BotController _controller;
    private readonly IAutomationSurface _surface;
    private readonly BotSettingsWindow _settings;
    private readonly NavigationWindow _navigation;
    private readonly MetaRulesWindow _metaRules;
    private readonly MonstersWindow _monsters;
    private readonly ItemsWindow _items;
    private readonly LogWindow _log;

    private bool _open = true;
    // Lock, collapse and opacity live in the profile (DashboardSettings), so
    // the window comes back the way it was left.
    private bool _minimized => _controller.Profile.Dashboard.Minimized;
    private bool _locked => _controller.Profile.Dashboard.Locked;
    private float _bgOpacity => Math.Clamp(_controller.Profile.Dashboard.Opacity, 0.1f, 1f);

    private void Dashboard(Func<DashboardSettings, DashboardSettings> change) =>
        _controller.Update(p => p with { Dashboard = change(p.Dashboard) });
    private Vector2 _expandedSize = new(430f, 452f);
    private bool _wasMinimized;
    private IReadOnlyList<string> _profileNames = [];
    private IReadOnlyList<string> _routeNames = [];
    private IReadOnlyList<string> _navFiles = [];
    private IReadOnlyList<string> _lootFiles = [];
    private IReadOnlyList<string> _metaFiles = [];
    private double _namesRefreshedAt = double.NegativeInfinity;
    private string _patrolMessage = string.Empty;
    private double _patrolMessageAt = double.NegativeInfinity;

    public BotDashboard(BotController controller, IAutomationSurface surface)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _settings = new BotSettingsWindow(controller);
        _navigation = new NavigationWindow(controller, surface);
        _metaRules = new MetaRulesWindow(controller, surface);
        _monsters = new MonstersWindow(controller, surface);
        _items = new ItemsWindow(controller, surface);
        _log = new LogWindow(controller);
    }

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        // The overlay draws from the login screen on; the bot has nothing to
        // show until there is a character in the world, and the windows would
        // sit over the character list otherwise.
        if (!_surface.IsAvailable || !_surface.Character.IsInWorld)
            return;
        _settings.Draw();
        _navigation.Draw();
        _metaRules.Draw();
        _monsters.Draw();
        _items.Draw();
        _log.Draw();
        if (!_open)
            return;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 10));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8.0f);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 6.0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.04f, 0.06f, 0.08f, _bgOpacity));
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse;
        if (_minimized)
        {
            flags |= ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoResize;
            _wasMinimized = true;
        }
        else if (_locked)
        {
            flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove;
        }

        ImGui.SetNextWindowSizeConstraints(new Vector2(400, 0), new Vector2(1200, 2000));
        if (!_minimized && !_locked)
        {
            if (_wasMinimized)
            {
                ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.Always);
                _wasMinimized = false;
            }
            else
            {
                ImGui.SetNextWindowSize(_expandedSize, ImGuiCond.FirstUseEver);
            }
        }
        else if (_minimized)
        {
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
        }

        if (ImGui.Begin("DrakBot Dashboard##Main", flags))
        {
            if (!_minimized && !_locked)
                _expandedSize = ImGui.GetWindowSize();
            RefreshNames();
            DrawHeader();
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ColPanelBg);
            ImGui.PushStyleColor(ImGuiCol.Border, ColBtnBord);
            ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1.0f);
            if (ImGui.BeginChild("CombatPanel", new Vector2(-1, 200), ImGuiChildFlags.Borders, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
            {
                DrawCombatPanel();
                ImGui.Dummy(new Vector2(0, 2));
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(2);
            if (!_minimized)
            {
                ImGui.Spacing();
                ImGui.Spacing();
                DrawLauncherGrid();
                if (_patrolMessage.Length > 0 && ImGui.GetTime() - _patrolMessageAt < 5d)
                    ImGui.TextColored(ColTextMute, _patrolMessage);
            }
        }
        ImGui.End();
        ImGui.PopStyleColor(1);
        ImGui.PopStyleVar(3);
    }

    // ── header ───────────────────────────────────────────────────────────

    private void DrawHeader()
    {
        BotEngine engine = _controller.Engine;
        float width = ImGui.GetContentRegionAvail().X;
        float startY = ImGui.GetCursorPosY();
        ImGui.SetWindowFontScale(1.4f);
        ImGui.TextColored(ColTeal, "D");
        ImGui.SameLine(0, 2);
        ImGui.TextColored(new Vector4(1, 1, 1, 1), "RAKBOT DASHBOARD");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.SameLine();
        ImGui.SetCursorPosY(startY + 5);
        ImGui.TextColored(ColTextMute, Version);
        ImGui.SameLine(width - 130);
        ImGui.SetCursorPosY(startY + 2);
        if (ImGui.SmallButton(_locked ? PhosphorIcons.Lock : PhosphorIcons.LockOpen))
            Dashboard(d => d with { Locked = !d.Locked });
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(_locked ? "Unlock Window" : "Lock Window");
        ImGui.SameLine();
        if (ImGui.SmallButton(PhosphorIcons.Minus))
            Dashboard(d => d with { Opacity = Math.Max(0.1f, d.Opacity - 0.1f) });
        ImGui.SameLine();
        if (ImGui.SmallButton(PhosphorIcons.Plus))
            Dashboard(d => d with { Opacity = Math.Min(1.0f, d.Opacity + 0.1f) });
        ImGui.SameLine();
        if (ImGui.SmallButton(_minimized ? PhosphorIcons.ArrowsOutLineVertical : PhosphorIcons.ArrowsInLineVertical))
            Dashboard(d => d with { Minimized = !d.Minimized });
        ImGui.SameLine();
        if (ImGui.SmallButton(PhosphorIcons.X))
            _open = false;
        ImGui.Dummy(new Vector2(0, 2));
        if (_minimized)
            return;
        if (!ImGui.BeginTable("HeaderGrid", 2))
            return;
        ImGui.TableSetupColumn("Left", ImGuiTableColumnFlags.WidthFixed, width * 0.40f);
        ImGui.TableSetupColumn("Right", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        // Left column: the run button, its light, the meta state and the activity.
        ImGui.TableNextColumn();
        bool running = engine.IsRunning;
        PushRunColors(running);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 4.0f);
        ImGui.SetWindowFontScale(1.2f);
        Vector2 pos = ImGui.GetCursorScreenPos();
        if (ImGui.Button(running ? "RUNNING##ToggleMacro" : "STOPPED##ToggleMacro", new Vector2(120, 28)))
            _controller.Toggle();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Click to Start / Stop the bot");
        ImGui.SetWindowFontScale(1.0f);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);
        ImDrawListPtr dl = ImGui.GetWindowDrawList();
        uint circleColor = ImGui.ColorConvertFloat4ToU32(running ? ColGreen : ColTextMute);
        Vector2 circlePos = pos + new Vector2(138, 14);
        dl.AddCircleFilled(circlePos, 5, circleColor);
        if (running)
            dl.AddCircle(circlePos, 8, circleColor, 12, 1.5f);

        ImGui.Spacing();
        ImGui.TextColored(ColTextMute, "Meta State:");
        ImGui.SameLine(0, 8);
        MetaEngine? meta = _controller.Meta;
        ImGui.TextColored(ColAmber, meta is { Rules.Count: > 0 } ? meta.CurrentState : "None");
        ImGui.TextColored(ColTextMute, "Bot Activity:");
        ImGui.SameLine(0, 8);
        string activity = running ? engine.ActiveBehaviorName : "Idle";
        ImGui.TextColored(ColAmber, activity == "idle" ? "Idle" : activity);
        if (ImGui.IsItemHovered() && engine.LastReason.Length > 0)
            ImGui.SetTooltip(engine.LastReason);

        // Right column: the file pickers.
        ImGui.TableNextColumn();
        ImGui.TextColored(ColTextMute, "Profile:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##ProfCombo", Truncate(_controller.Profile.Name, 16)))
        {
            foreach (string profile in _profileNames)
            {
                if (ImGui.Selectable(profile, profile == _controller.Profile.Name))
                    _controller.LoadProfile(profile);
            }
            if (_profileNames.Count == 0)
                ImGui.TextDisabled("no saved profiles");
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColTextMute, "Nav:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        Route? route = _controller.Navigation.Route;
        if (ImGui.BeginCombo("##NavCombo", Truncate(route?.Name ?? "None", 16)))
        {
            if (ImGui.Selectable("None", route is null))
                _controller.Navigation.SetRoute(null);
            foreach (string name in _routeNames)
            {
                if (ImGui.Selectable(name, name == route?.Name))
                    _controller.LoadRouteByName(name);
            }
            foreach (string file in _navFiles)
            {
                string name = file[..^4];
                if (ImGui.Selectable(file, name == route?.Name))
                    _controller.LoadRouteByName(name);
            }
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColTextMute, "Loot:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        string loot = _controller.Profile.Loot.UtlProfile;
        if (ImGui.BeginCombo("##LootCombo", Truncate(loot.Length == 0 ? "None" : loot, 16)))
        {
            if (ImGui.Selectable("None", loot.Length == 0))
                _controller.Update(p => p with { Loot = p.Loot with { UtlProfile = string.Empty } });
            foreach (string file in _lootFiles)
            {
                string name = file[..^4];
                if (ImGui.Selectable(file, name.Equals(loot, StringComparison.OrdinalIgnoreCase)))
                    _controller.Update(p => p with { Loot = p.Loot with { UtlProfile = name } });
            }
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColTextMute, "Meta:");
        ImGui.SameLine(60);
        ImGui.SetNextItemWidth(-1);
        string metaName = meta is { MetaName.Length: > 0 } ? meta.MetaName : "None";
        if (ImGui.BeginCombo("##MetaCombo", Truncate(metaName, 16)))
        {
            if (ImGui.Selectable("None", metaName == "None") && meta is not null)
            {
                meta.Clear();
                _controller.Update(p => p with { Meta = p.Meta with { Name = string.Empty } });
            }
            foreach (string file in _metaFiles)
            {
                string name = file[..file.LastIndexOf('.')];
                if (ImGui.Selectable(file, name.Equals(metaName, StringComparison.OrdinalIgnoreCase)) && meta is not null)
                    meta.LoadByName(name);
            }
            ImGui.EndCombo();
        }
        ImGui.EndTable();
        ImGui.Spacing();
    }

    // ── combat panel ─────────────────────────────────────────────────────

    private void DrawCombatPanel()
    {
        if (!ImGui.BeginTable("CombatInnerTable", 2, ImGuiTableFlags.None))
            return;
        ImGui.TableSetupColumn("Toggles", ImGuiTableColumnFlags.WidthFixed, 68);
        ImGui.TableSetupColumn("Vitals", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        BotProfile profile = _controller.Profile;
        bool running = _controller.Engine.IsRunning;
        if (_minimized)
        {
            PushRunColors(running);
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
            if (ImGui.Button(running ? "ON##MinMacro" : "OFF##MinMacro", new Vector2(64, 20)))
                _controller.Toggle();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(running ? "Bot Running - Click to Stop" : "Bot Stopped - Click to Start");
            ImGui.PopStyleVar();
            ImGui.PopStyleColor(3);
        }
        Vector2 togglePos = ImGui.GetCursorScreenPos() + new Vector2(2, _minimized ? 6 : 28);

        // Left-click toggles; right-click opens the matching settings or window.
        if (SquareToggle(PhosphorIcons.Sword, profile.Combat.Enabled, togglePos, "CombatTgl"))
            _controller.Update(p => p with { Combat = p.Combat with { Enabled = !p.Combat.Enabled } });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _settings.Open("Combat");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Combat - left-click to toggle, right-click for settings");

        if (SquareToggle(PhosphorIcons.Sparkle, profile.Buffs.Enabled, togglePos + new Vector2(34, 0), "BuffTgl"))
            _controller.Update(p => p with { Buffs = p.Buffs with { Enabled = !p.Buffs.Enabled } });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _settings.Open("Buffing");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Buffing - left-click to toggle, right-click for settings");

        if (SquareToggle(PhosphorIcons.SneakerMove, profile.Navigation.Enabled, togglePos + new Vector2(0, 34), "NavTgl"))
            _controller.Update(p => p with { Navigation = p.Navigation with { Enabled = !p.Navigation.Enabled } });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _navigation.IsOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Navigation - left-click to toggle, right-click for routes");

        if (SquareToggle(PhosphorIcons.Bag, profile.Loot.Enabled, togglePos + new Vector2(34, 34), "LootTgl"))
            _controller.Update(p => p with { Loot = p.Loot with { Enabled = !p.Loot.Enabled } });
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _settings.Open("Looting");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Looting - left-click to toggle, right-click for settings");

        if (WideToggle("MACRO", PhosphorIcons.Code, profile.Meta.Enabled, togglePos + new Vector2(0, 68), "MetaTgl", 64f, 20f) && _controller.Meta is { } metaEngine)
            metaEngine.Enabled = !profile.Meta.Enabled;
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
            _metaRules.IsOpen = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Macro / Meta - left-click to toggle, right-click for rules");

        // FR (force rebuff) below the MACRO toggle.
        Vector2 frPos = togglePos + new Vector2(0, 92);
        ImGui.SetCursorScreenPos(frPos);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.28f, 0.20f, 0.04f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.46f, 0.33f, 0.06f, 1.00f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.20f, 0.14f, 0.03f, 1.00f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3.0f);
        bool forcing = _controller.Buffs.IsForceRebuffPending;
        if (ImGui.Button(forcing ? "FR..##ForceRebuff" : "FR##ForceRebuff", new Vector2(64, 16)) && !forcing)
            _controller.Buffs.ForceRebuff();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Force-recast all buffs.");
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(3);

        // Right column: the target, then the player's vitals.
        ImGui.TableNextColumn();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
        bool hasTarget = TryCurrentTarget(out PluginCombatTarget target);
        string targetLabel = hasTarget ? target.Name : "NO TARGET";
        string targetHealth = !hasTarget ? "0" : target.IsHealthKnown ? $"{target.HealthFraction:P0}" : "--";
        float targetPct = hasTarget && target.IsHealthKnown ? target.HealthFraction : 0f;
        ImGui.TextColored(ColTextDim, Truncate(targetLabel, 32).ToUpperInvariant());
        ImGui.SameLine();
        float valueWidth = ImGui.CalcTextSize(targetHealth).X;
        float lineX = ImGui.GetCursorPosX();
        float regionWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(lineX + Math.Max(0, regionWidth - valueWidth));
        ImGui.TextColored(new Vector4(1, 1, 1, 1), targetHealth);
        SegmentedBar(targetPct, ImGui.GetContentRegionAvail().X - 4);
        if (hasTarget)
        {
            float barWidth = ImGui.GetContentRegionAvail().X - 4;
            CompactVitalBar("DIST", 1f, ColBarBg, $"{target.Distance:0.0}m", barWidth);
        }
        ImGui.Dummy(new Vector2(0, 2));
        ImGui.TextColored(ColTextMute, "PLAYER VITALS");
        ICharacterInfo character = _surface.Character;
        VitalRow(PhosphorIcons.Heart, "HP", Ratio(character.CurrentHealth, character.MaxHealth), ColHp, FormatVital(character.CurrentHealth, character.MaxHealth));
        VitalRow(PhosphorIcons.PersonSimpleRun, "ST", Ratio(character.CurrentStamina, character.MaxStamina), ColGreen, FormatVital(character.CurrentStamina, character.MaxStamina));
        VitalRow(PhosphorIcons.Drop, "MN", Ratio(character.CurrentMana, character.MaxMana), ColMana, FormatVital(character.CurrentMana, character.MaxMana));
        ImGui.EndTable();
    }

    // ── launcher grid ────────────────────────────────────────────────────

    private void DrawLauncherGrid()
    {
        if (!ImGui.BeginTable("LauncherGridTable", 3, ImGuiTableFlags.SizingStretchSame))
            return;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (GridButton("Macro Rules", PhosphorIcons.ListChecks, _metaRules.IsOpen))
            _metaRules.IsOpen = !_metaRules.IsOpen;
        ImGui.TableNextColumn();
        if (GridButton("Monsters", PhosphorIcons.Skull, _monsters.IsOpen))
            _monsters.IsOpen = !_monsters.IsOpen;
        ImGui.TableNextColumn();
        if (GridButton("Settings", PhosphorIcons.Gear, _settings.IsOpen))
            _settings.IsOpen = !_settings.IsOpen;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        if (GridButton("Navigation", PhosphorIcons.MapTrifold, _navigation.IsOpen))
            _navigation.IsOpen = !_navigation.IsOpen;
        ImGui.TableNextColumn();
        if (GridButton("Items", PhosphorIcons.Backpack, _items.IsOpen))
            _items.IsOpen = !_items.IsOpen;
        ImGui.TableNextColumn();
        if (GridButton("Log", PhosphorIcons.TerminalWindow, _log.IsOpen))
            _log.IsOpen = !_log.IsOpen;
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        bool patrolling = _controller.IsPatrolling;
        if (GridButton(patrolling ? "Stop Patrol" : "Dungeon Patrol", PhosphorIcons.Footprints, patrolling))
        {
            if (patrolling)
            {
                _controller.ClearRoute();
                _patrolMessage = "patrol stopped";
            }
            else if (_controller.TryStartPatrol(out _patrolMessage))
            {
                _controller.Engine.Start();
            }
            _patrolMessageAt = ImGui.GetTime();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(patrolling ? "Stop the dungeon patrol" : "Patrol this dungeon: a circular hunt through every cell, avoiding marked hazards; starts the bot");
        ImGui.TableNextColumn();
        if (GridButton("Save Profile", PhosphorIcons.FloppyDisk, false))
            _controller.SaveProfile();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Save the live profile as '{_controller.Profile.Name}'");
        ImGui.TableNextColumn();
        ImGui.EndTable();
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private void RefreshNames()
    {
        double now = ImGui.GetTime();
        if (now - _namesRefreshedAt < 3d)
            return;
        _namesRefreshedAt = now;
        _profileNames = _controller.Store.ProfileNames();
        _routeNames = _controller.Store.RouteNames();
        _navFiles = _controller.NavFileNames();
        _lootFiles = _controller.UtlFileNames();
        _metaFiles = _controller.MetaFileNames();
    }

    private bool TryCurrentTarget(out PluginCombatTarget target)
    {
        target = default;
        Blackboard? board = _controller.Engine.LastBoard;
        CombatBehavior? combat = _controller.Engine.Behaviors.OfType<CombatBehavior>().FirstOrDefault();
        if (board is null)
            return false;
        uint targetId = combat?.CurrentTargetId ?? 0u;
        if (targetId == 0u)
            targetId = board.Combat.SelectedObjectId;
        foreach (PluginCombatTarget hostile in board.Hostiles)
        {
            if (hostile.ObjectId == targetId)
            {
                target = hostile;
                return true;
            }
        }
        return false;
    }

    private static void PushRunColors(bool running)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, running ? new Vector4(0.10f, 0.35f, 0.15f, 1f) : new Vector4(0.25f, 0.12f, 0.12f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, running ? new Vector4(0.15f, 0.50f, 0.22f, 1f) : new Vector4(0.40f, 0.18f, 0.18f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, running ? new Vector4(0.08f, 0.28f, 0.12f, 1f) : new Vector4(0.20f, 0.10f, 0.10f, 1f));
    }

    private static float Ratio(uint value, uint maximum) => maximum == 0u ? 0f : Math.Clamp((float)value / maximum, 0f, 1f);

    private static string FormatVital(uint value, uint maximum) =>
        maximum == 0u ? (value == 0u ? "--/--" : $"{value}/--") : $"{value}/{maximum}";

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length > max ? value[..(max - 1)] + "..." : value;
}
