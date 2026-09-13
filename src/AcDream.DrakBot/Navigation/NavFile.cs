using System.Globalization;
using System.Text;

namespace AcDream.DrakBot.Navigation;

/// <summary>
/// The VTank / uTank2 <c>.nav</c> route file, read and written line for
/// line so a route made with those tools walks here unchanged. The layout
/// is the one seen in real route files: a three-line header (magic, route
/// type, point count) and then, per point, five lines (kind, east-west,
/// north-south, elevation, a flag) followed by the kind's trailer.
/// </summary>
public static class NavFile
{
    public const string Magic = "uTank2 NAV 1.2";

    // The file's route types.
    private const int Circular = 1;
    private const int Linear = 2;
    private const int Once = 4;

    public static bool IsKnownKind(int kind) => Enum.IsDefined(typeof(WaypointKind), kind);

    /// <summary>
    /// Trailer lines after a point's five-line prologue, per kind. The one
    /// place this is known, so a meta's embedded route is counted the same
    /// way it is read.
    /// </summary>
    public static int TrailerLineCount(WaypointKind kind) => kind switch
    {
        WaypointKind.Recall or WaypointKind.Pause or WaypointKind.Chat => 1,
        WaypointKind.Vendor => 2,
        WaypointKind.Portal or WaypointKind.Npc => 6,
        _ => 0,
    };

    /// <summary>
    /// Parses the file text. A point that cannot be read ends the route with
    /// a warning; what came before it stands.
    /// </summary>
    public static Route Parse(string name, IReadOnlyList<string> lines, out string? warning)
    {
        warning = null;
        if (lines.Count < 3 || !lines[0].Contains(Magic, StringComparison.OrdinalIgnoreCase))
        {
            warning = "not a uTank2 NAV 1.2 file";
            return new Route { Name = name };
        }
        if (!TryInt(lines[1], out int routeType) || !TryInt(lines[2], out int count))
        {
            warning = "malformed header: route type and point count must be integers";
            return new Route { Name = name };
        }

        var waypoints = new List<Waypoint>(Math.Max(0, count));
        int line = 3;
        for (int index = 0; index < count && line < lines.Count; index++)
        {
            int start = line;
            try
            {
                int kindRaw = Int(lines[line++]);
                if (!Enum.IsDefined(typeof(WaypointKind), kindRaw))
                {
                    warning = $"unknown waypoint type {kindRaw} at point {index + 1} (line {start + 1}); stopped with {waypoints.Count} points";
                    break;
                }
                var kind = (WaypointKind)kindRaw;
                double eastWest = Double(lines[line++]);
                double northSouth = Double(lines[line++]);
                double elevation = Double(lines[line++]);
                line++; // the flag / colour line
                var waypoint = new Waypoint(kind, eastWest, northSouth) { Elevation = elevation };
                switch (kind)
                {
                    case WaypointKind.Recall:
                        waypoint = waypoint with { SpellId = (uint)Int(lines[line++]) };
                        break;
                    case WaypointKind.Pause:
                        waypoint = waypoint with { Seconds = Int(lines[line++]) / 1000d };
                        break;
                    case WaypointKind.Chat:
                        waypoint = waypoint with { Text = lines[line++] };
                        break;
                    case WaypointKind.Vendor:
                        waypoint = waypoint with
                        {
                            VendorId = uint.Parse(lines[line++].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture),
                            TargetName = lines[line++],
                        };
                        break;
                    case WaypointKind.Portal:
                    case WaypointKind.Npc:
                        string targetName = lines[line++];
                        line++; // object class
                        line++; // tie flag
                        double targetEastWest = Double(lines[line++]);
                        double targetNorthSouth = Double(lines[line++]);
                        double targetElevation = Double(lines[line++]);
                        waypoint = waypoint with
                        {
                            TargetName = targetName,
                            TargetEastWest = targetEastWest,
                            TargetNorthSouth = targetNorthSouth,
                            TargetElevation = targetElevation,
                        };
                        break;
                }
                waypoints.Add(waypoint);
            }
            catch (Exception error) when (error is FormatException or OverflowException or ArgumentOutOfRangeException)
            {
                warning = $"could not read point {index + 1} (line {start + 1}): {error.Message}; stopped with {waypoints.Count} points";
                break;
            }
        }

        return new Route
        {
            Name = name,
            Mode = routeType switch
            {
                Circular => RouteMode.Loop,
                Linear => RouteMode.PingPong,
                _ => RouteMode.Once,
            },
            Waypoints = waypoints,
        };
    }

    public static string Write(Route route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var text = new StringBuilder();
        text.Append(Magic).Append('\n');
        text.Append(route.Mode switch
        {
            RouteMode.Loop => Circular,
            RouteMode.PingPong => Linear,
            _ => Once,
        }).Append('\n');
        text.Append(route.Waypoints.Count).Append('\n');
        foreach (Waypoint waypoint in route.Waypoints)
        {
            text.Append((int)waypoint.Kind).Append('\n');
            text.Append(Str(waypoint.EastWest)).Append('\n');
            text.Append(Str(waypoint.NorthSouth)).Append('\n');
            text.Append(Str(waypoint.Elevation)).Append('\n');
            text.Append("0\n");
            switch (waypoint.Kind)
            {
                case WaypointKind.Recall:
                    text.Append(waypoint.SpellId).Append('\n');
                    break;
                case WaypointKind.Pause:
                    text.Append((int)Math.Round(waypoint.Seconds * 1000d)).Append('\n');
                    break;
                case WaypointKind.Chat:
                    text.Append(waypoint.Text).Append('\n');
                    break;
                case WaypointKind.Vendor:
                    text.Append(waypoint.VendorId).Append('\n');
                    text.Append(waypoint.TargetName).Append('\n');
                    break;
                case WaypointKind.Portal:
                case WaypointKind.Npc:
                    text.Append(waypoint.TargetName).Append('\n');
                    text.Append(waypoint.Kind == WaypointKind.Portal ? 14 : 37).Append('\n');
                    text.Append("False\n");
                    text.Append(Str(waypoint.TargetEastWest)).Append('\n');
                    text.Append(Str(waypoint.TargetNorthSouth)).Append('\n');
                    text.Append(Str(waypoint.TargetElevation)).Append('\n');
                    break;
            }
        }
        return text.ToString();
    }

    private static bool TryInt(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static int Int(string text) =>
        int.Parse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static double Double(string text) =>
        double.Parse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string Str(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
