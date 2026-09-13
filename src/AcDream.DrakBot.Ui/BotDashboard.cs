using System.Numerics;
using AcDream.DrakBot.Behaviors;
using AcDream.DrakBot.Combat;
using AcDream.DrakBot.Navigation;
using AcDream.DrakBot.Profiles;
using AcDream.Plugin.Abstractions;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// The main window: run state and activity on the left, profile and route
/// pickers on the right, the four subsystem toggles, the player's vitals and
/// the current target. Everything else opens from here.
/// </summary>
public sealed class BotDashboard
{
    private static readonly Vector4 ColMuted = new(0.55f, 0.63f, 0.71f, 1f);
    private static readonly Vector4 ColAccent = new(0.15f, 0.85f, 0.90f, 1f);
    private static readonly Vector4 ColRunning = new(0.20f, 0.65f, 0.30f, 1f);
    private static readonly Vector4 ColStopped = new(0.55f, 0.20f, 0.20f, 1f);
    private static readonly Vector4 ColHealth = new(0.80f, 0.20f, 0.22f, 1f);
    private static readonly Vector4 ColStamina = new(0.25f, 0.70f, 0.30f, 1f);
    private static readonly Vector4 ColMana = new(0.25f, 0.45f, 0.90f, 1f);
    private static readonly Vector4 ColToggleOn = new(0.13f, 0.40f, 0.55f, 1f);
    private static readonly Vector4 ColToggleOff = new(0.10f, 0.13f, 0.17f, 1f);

    private readonly BotController _controller;
    private readonly IAutomationSurface _surface;
    private readonly BotSettingsWindow _settings;
    private readonly NavBuilderWindow _navBuilder;
    private bool _open = true;
    private IReadOnlyList<string> _profileNames = [];
    private IReadOnlyList<string> _routeNames = [];
    private double _namesRefreshedAt = double.NegativeInfinity;

    public BotDashboard(BotController controller, IAutomationSurface surface)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        _settings = new BotSettingsWindow(controller);
        _navBuilder = new NavBuilderWindow(controller);
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
        _navBuilder.Draw();
        if (!_open)
            return;

        ImGui.SetNextWindowSize(new Vector2(380f, 0f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("DrakBot", ref _open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        RefreshNames();
        DrawHeader();
        ImGui.Separator();
        DrawToggles();
        ImGui.Separator();
        DrawVitals();
        ImGui.Separator();
        DrawLaunchers();
        ImGui.End();
    }

    private void RefreshNames()
    {
        double now = ImGui.GetTime();
        if (now - _namesRefreshedAt < 2d)
            return;
        _namesRefreshedAt = now;
        _profileNames = _controller.Store.ProfileNames();
        _routeNames = _controller.Store.RouteNames();
    }

    private void DrawHeader()
    {
        BotEngine engine = _controller.Engine;
        if (!ImGui.BeginTable("header", 2))
            return;
        ImGui.TableSetupColumn("left", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("right", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();

        ImGui.TableSetColumnIndex(0);
        ImGui.PushStyleColor(ImGuiCol.Button, engine.IsRunning ? ColRunning : ColStopped);
        if (ImGui.Button(engine.IsRunning ? "RUNNING" : "STOPPED", new Vector2(140f, 30f)))
            _controller.Toggle();
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Start / stop DrakBot");
        ImGui.Spacing();
        ImGui.TextColored(ColMuted, "Activity");
        ImGui.SameLine(70f);
        ImGui.TextColored(ColAccent, engine.IsRunning ? engine.ActiveBehaviorName : "idle");
        ImGui.TextColored(ColMuted, "Reason");
        ImGui.SameLine(70f);
        ImGui.TextWrapped(engine.LastReason);

        ImGui.TableSetColumnIndex(1);
        ImGui.TextColored(ColMuted, "Profile");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##profile", _controller.Profile.Name))
        {
            foreach (string name in _profileNames)
            {
                if (ImGui.Selectable(name, name == _controller.Profile.Name))
                    _controller.LoadProfile(name);
            }
            if (_profileNames.Count == 0)
                ImGui.TextDisabled("no saved profiles");
            ImGui.EndCombo();
        }

        ImGui.TextColored(ColMuted, "Route");
        Route? route = _controller.Navigation.Route;
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##route", route?.Name ?? "none"))
        {
            if (ImGui.Selectable("none", route is null))
                _controller.Navigation.SetRoute(null);
            foreach (string name in _routeNames)
            {
                if (ImGui.Selectable(name, name == route?.Name))
                    _controller.LoadRoute(name);
            }
            ImGui.EndCombo();
        }
        if (route is not null)
        {
            ImGui.TextColored(
                ColMuted,
                $"waypoint {_controller.Navigation.WaypointIndex + 1}/{route.Waypoints.Count}");
        }
        ImGui.EndTable();
    }

    private void DrawToggles()
    {
        BotProfile profile = _controller.Profile;
        var size = new Vector2(84f, 26f);
        if (ToggleButton("Combat", profile.Combat.Enabled, size))
            _controller.Update(p => p with { Combat = p.Combat with { Enabled = !p.Combat.Enabled } });
        ImGui.SameLine();
        if (ToggleButton("Buffs", profile.Buffs.Enabled, size))
            _controller.Update(p => p with { Buffs = p.Buffs with { Enabled = !p.Buffs.Enabled } });
        ImGui.SameLine();
        if (ToggleButton("Loot", profile.Loot.Enabled, size))
            _controller.Update(p => p with { Loot = p.Loot with { Enabled = !p.Loot.Enabled } });
        ImGui.SameLine();
        if (ToggleButton("Nav", profile.Navigation.Enabled, size))
            _controller.Update(p => p with { Navigation = p.Navigation with { Enabled = !p.Navigation.Enabled } });

        ImGui.Spacing();
        bool forcing = _controller.Buffs.IsForceRebuffPending;
        if (ImGui.Button(forcing ? "Rebuffing..." : "Rebuff all", new Vector2(120f, 22f)) && !forcing)
            _controller.Buffs.ForceRebuff();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Recast every configured buff now");
        ImGui.SameLine();
        ImGui.TextColored(ColMuted, $"style: {profile.Combat.Style}");
    }

    private void DrawVitals()
    {
        ICharacterInfo character = _surface.Character;
        ImGui.TextColored(ColMuted, "PLAYER");
        VitalBar("HP", character.CurrentHealth, character.MaxHealth, ColHealth);
        VitalBar("ST", character.CurrentStamina, character.MaxStamina, ColStamina);
        VitalBar("MN", character.CurrentMana, character.MaxMana, ColMana);

        ImGui.Spacing();
        ImGui.TextColored(ColMuted, "TARGET");
        if (TryCurrentTarget(out PluginCombatTarget target, out CombatBehavior? combat))
        {
            ImGui.Text($"{target.Name}  ({target.Distance:0.0} m)");
            float fraction = target.IsHealthKnown ? target.HealthFraction : 0f;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, ColHealth);
            ImGui.ProgressBar(fraction, new Vector2(-1f, 14f), target.IsHealthKnown ? $"{fraction:P0}" : "?");
            ImGui.PopStyleColor();
            DrawLineOfSight(target.ObjectId, combat);
        }
        else
        {
            ImGui.TextDisabled("none");
        }
    }

    private void DrawLineOfSight(uint targetId, CombatBehavior? combat)
    {
        ImGui.TextColored(ColMuted, "LoS");
        ImGui.SameLine(36f);
        if (combat is null)
        {
            ImGui.TextDisabled("n/a");
            return;
        }
        LineOfSightService los = combat.LineOfSight;
        if (!los.IsEnabled)
        {
            ImGui.TextDisabled("off");
            return;
        }
        if (los.IsBlacklisted(targetId))
        {
            ImGui.TextColored(ColHealth, $"blacklisted {los.BlacklistSecondsRemaining(targetId):0}s");
            return;
        }
        if (_controller.Profile.Combat.Style == CombatStyle.Melee)
        {
            DrawWalk(targetId, combat, los);
            return;
        }
        if (!los.TryGetLast(targetId, out LineOfSightVerdict verdict))
        {
            ImGui.TextDisabled("not checked");
            return;
        }
        string state = verdict.State switch
        {
            LineOfSightState.Clear => $"clear ({verdict.Kind}, {verdict.Height})",
            LineOfSightState.Blocked => verdict.BlockingObjectId == 0u
                ? $"blocked ({verdict.Kind})"
                : $"blocked by 0x{verdict.BlockingObjectId:X8} ({verdict.Kind})",
            _ => $"unknown ({verdict.Status})",
        };
        Vector4 color = verdict.State switch
        {
            LineOfSightState.Clear => ColStamina,
            LineOfSightState.Blocked => ColHealth,
            _ => ColMuted,
        };
        ImGui.TextColored(color, state);
        int strikes = los.StrikesFor(targetId);
        if (strikes > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"strike {strikes}/{_controller.Profile.Combat.LineOfSight.BlacklistStrikes}");
        }
        if (combat.IsApproaching)
        {
            ImGui.TextColored(ColMuted, "Walk");
            ImGui.SameLine(36f);
            DrawWalk(targetId, combat, los);
        }
    }

    /// <summary>The walk verdict: which heading is open, or that none is.</summary>
    private void DrawWalk(uint targetId, CombatBehavior combat, LineOfSightService los)
    {
        if (!los.ChecksWalking)
        {
            ImGui.TextDisabled(combat.IsApproaching ? "closing in (unchecked)" : "n/a");
            return;
        }
        if (!los.TryGetLastWalk(targetId, out WalkVerdict walk))
        {
            ImGui.TextDisabled(combat.IsApproaching ? "closing in" : "not checked");
            return;
        }
        string text = walk.State switch
        {
            LineOfSightState.Clear => float.IsNaN(combat.ApproachHeadingDegrees)
                ? "path open"
                : $"walking {combat.ApproachHeadingDegrees:0}\u00b0",
            LineOfSightState.Blocked => walk.BlockingObjectId == 0u
                ? "no open heading"
                : $"no open heading (0x{walk.BlockingObjectId:X8})",
            _ => "unknown",
        };
        ImGui.TextColored(walk.State == LineOfSightState.Blocked ? ColHealth : ColStamina, text);
        int strikes = los.StrikesFor(targetId);
        if (strikes > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(ColMuted, $"strike {strikes}/{_controller.Profile.Combat.LineOfSight.BlacklistStrikes}");
        }
    }

    private void DrawLaunchers()
    {
        if (ImGui.Button("Settings", new Vector2(110f, 24f)))
            _settings.IsOpen = !_settings.IsOpen;
        ImGui.SameLine();
        if (ImGui.Button("Nav builder", new Vector2(110f, 24f)))
            _navBuilder.IsOpen = !_navBuilder.IsOpen;
        ImGui.SameLine();
        if (ImGui.Button("Save profile", new Vector2(110f, 24f)))
            _controller.SaveProfile();
    }

    private bool TryCurrentTarget(out PluginCombatTarget target, out CombatBehavior? combat)
    {
        target = default;
        Blackboard? board = _controller.Engine.LastBoard;
        combat = _controller.Engine.Behaviors.OfType<CombatBehavior>().FirstOrDefault();
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

    private static void VitalBar(string label, uint current, uint max, Vector4 color)
    {
        float fraction = max == 0u ? 0f : Math.Clamp((float)current / max, 0f, 1f);
        ImGui.TextColored(ColMuted, label);
        ImGui.SameLine(36f);
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
        ImGui.ProgressBar(fraction, new Vector2(-1f, 14f), $"{current}/{max}");
        ImGui.PopStyleColor();
    }

    private static bool ToggleButton(string label, bool on, Vector2 size)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, on ? ColToggleOn : ColToggleOff);
        ImGui.PushStyleColor(ImGuiCol.Text, on ? new Vector4(1f, 1f, 1f, 1f) : ColMuted);
        bool clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor(2);
        return clicked;
    }
}
