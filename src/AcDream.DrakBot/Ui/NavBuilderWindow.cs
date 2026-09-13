using System.Globalization;
using System.Numerics;
using AcDream.DrakBot.Navigation;
using ImGuiNET;

namespace AcDream.DrakBot.Ui;

/// <summary>
/// Records and edits the draft route: drop a waypoint where the character
/// stands, add pauses, remove steps, then follow it or save it by name.
/// </summary>
public sealed class NavBuilderWindow(BotController controller)
{
    private bool _open;
    private string _routeName = string.Empty;
    private float _pauseSeconds = 2f;
    private int _selected = -1;

    public bool IsOpen
    {
        get => _open;
        set => _open = value;
    }

    public void Draw()
    {
        if (!_open)
            return;
        ImGui.SetNextWindowSize(new Vector2(380f, 360f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Nav builder", ref _open))
        {
            ImGui.End();
            return;
        }

        Route draft = controller.DraftRoute;
        if (ImGui.Button("Add waypoint here", new Vector2(150f, 24f)))
            controller.TryAddWaypoint(out _);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(70f);
        ImGui.InputFloat("##pause", ref _pauseSeconds, 0f, 0f, "%.1f");
        ImGui.SameLine();
        if (ImGui.Button("Add pause"))
            controller.AddPause(Math.Max(0.1f, _pauseSeconds));

        if (ImGui.BeginTable("waypoints", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY, new Vector2(-1f, 200f)))
        {
            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 30f);
            ImGui.TableSetupColumn("Step", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Detail", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableHeadersRow();
            for (int index = 0; index < draft.Waypoints.Count; index++)
            {
                Waypoint waypoint = draft.Waypoints[index];
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                bool current = controller.Navigation.Route == draft
                    && controller.Navigation.WaypointIndex == index;
                if (ImGui.Selectable($"{index + 1}##wp{index}", _selected == index, ImGuiSelectableFlags.SpanAllColumns))
                    _selected = index;
                ImGui.TableSetColumnIndex(1);
                ImGui.Text(waypoint.Kind == WaypointKind.Pause ? "Pause" : "Point");
                if (current)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.15f, 0.85f, 0.90f, 1f), "<-");
                }
                ImGui.TableSetColumnIndex(2);
                ImGui.Text(waypoint.Kind == WaypointKind.Pause
                    ? $"{waypoint.Seconds:0.#} s"
                    : $"{Coordinate(waypoint.NorthSouth, 'N', 'S')}, {Coordinate(waypoint.EastWest, 'E', 'W')}");
            }
            ImGui.EndTable();
        }

        ImGui.BeginDisabled(_selected < 0 || _selected >= draft.Waypoints.Count);
        if (ImGui.Button("Remove"))
        {
            controller.RemoveWaypoint(_selected);
            _selected = -1;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            controller.ClearRoute();
            _selected = -1;
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(draft.IsEmpty);
        if (ImGui.Button("Follow draft"))
            controller.UseDraftRoute();
        ImGui.EndDisabled();

        ImGui.Separator();
        ImGui.SetNextItemWidth(200f);
        ImGui.InputText("##routename", ref _routeName, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(draft.IsEmpty || string.IsNullOrWhiteSpace(_routeName));
        if (ImGui.Button("Save route"))
            controller.SaveRoute(_routeName.Trim());
        ImGui.EndDisabled();
        ImGui.TextDisabled($"draft: {draft.Waypoints.Count} steps" + (draft.Name == "draft" ? string.Empty : $" (from '{draft.Name}')"));
        ImGui.End();
    }

    private static string Coordinate(double value, char positive, char negative) =>
        $"{Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture)}{(value >= 0d ? positive : negative)}";
}
