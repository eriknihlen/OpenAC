using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class VtankNavRouteSerializer
{
    private const string Header = "uTank2 NAV 1.2";

    public static bool TryLoad(
        string source,
        NavigationSettings target,
        ISpellCatalog spells,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(spells);
        try
        {
            string nav = UnwrapEmbedded(source);
            using var reader = new StringReader(nav);
            if (!ReadLine(reader).Equals(Header, StringComparison.Ordinal))
                throw new FormatException("Nav file version does not match uTank2 NAV 1.2.");

            var parsed = new NavigationSettings
            {
                Enabled = target.Enabled,
                Priority = target.Priority,
                MinimumDistanceMeters = target.MinimumDistanceMeters,
                FollowAroundCorners = target.FollowAroundCorners,
                OpenDoors = target.OpenDoors,
                Mode = ReadInt(reader) switch
                {
                    1 => RouteMode.Circular,
                    2 => RouteMode.Linear,
                    3 => RouteMode.Target,
                    4 => RouteMode.Once,
                    _ => throw new FormatException("Unknown VTank navigation type."),
                },
            };

            if (parsed.Mode == RouteMode.Target)
            {
                parsed.FollowTargetName = ReadLine(reader);
                parsed.FollowTargetObjectId = unchecked((uint)ReadInt(reader));
            }
            else
            {
                int count = ReadInt(reader);
                if (count is < 0 or > 100_000)
                    throw new FormatException("Invalid VTank waypoint count.");
                for (int index = 0; index < count; index++)
                    parsed.Waypoints.Add(ReadWaypoint(reader, spells));
            }

            Apply(parsed, target);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or OverflowException or EndOfStreamException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static RouteWaypoint ReadWaypoint(
        TextReader reader,
        ISpellCatalog spells)
    {
        int type = ReadInt(reader);
        double eastWest = ReadDouble(reader);
        double northSouth = ReadDouble(reader);
        double elevation = ReadDouble(reader);
        _ = ReadLine(reader);
        var waypoint = new RouteWaypoint
        {
            Type = type switch
            {
                0 => RouteWaypointType.Point,
                1 => RouteWaypointType.Portal,
                2 => RouteWaypointType.Recall,
                3 => RouteWaypointType.Pause,
                4 => RouteWaypointType.ChatCommand,
                5 => RouteWaypointType.OpenVendor,
                6 => RouteWaypointType.PortalByName,
                7 => RouteWaypointType.UseNpc,
                8 => RouteWaypointType.Checkpoint,
                9 => RouteWaypointType.Jump,
                _ => throw new FormatException($"Unknown VTank waypoint type {type}."),
            },
            Position = Position(eastWest, northSouth, elevation),
        };

        switch (type)
        {
            case 1:
                waypoint.ObjectId = unchecked((uint)ReadInt(reader));
                break;
            case 2:
                waypoint.RecallSpellId = checked((uint)ReadInt(reader));
                if (spells.TryGet(waypoint.RecallSpellId, out PluginSpellInfo spell))
                    waypoint.RecallSpellName = spell.Name;
                break;
            case 3:
                waypoint.DurationMilliseconds = ReadInt(reader);
                break;
            case 4:
                waypoint.Text = ReadLine(reader);
                break;
            case 5:
                waypoint.ObjectId = unchecked((uint)ReadInt(reader));
                waypoint.ObjectName = ReadLine(reader);
                break;
            case 6:
            case 7:
                waypoint.ObjectName = ReadLine(reader);
                waypoint.LegacyObjectClass = ReadInt(reader);
                waypoint.LegacyReferenceValid = ReadBoolean(reader);
                double referenceEastWest = ReadDouble(reader);
                double referenceNorthSouth = ReadDouble(reader);
                double referenceElevation = ReadDouble(reader);
                waypoint.ReferencePosition = Position(
                    referenceEastWest,
                    referenceNorthSouth,
                    referenceElevation);
                break;
            case 9:
                waypoint.JumpHeadingDegrees = checked((float)ReadDouble(reader));
                waypoint.JumpRun = ReadBoolean(reader);
                ParseJump(ReadLine(reader), waypoint);
                break;
        }
        return waypoint;
    }

    private static void ParseJump(string source, RouteWaypoint target)
    {
        string value = source.Trim();
        char suffix = value.Length == 0 ? '\0' : value[^1];
        bool encoded = suffix is '3' or '4' or '5'
            && value.Length >= 6
            && value[^6] == '.';
        string milliseconds = encoded ? value[..^1] : value;
        target.JumpChargeMilliseconds = checked((int)Math.Round(
            double.Parse(milliseconds, NumberStyles.Float, CultureInfo.InvariantCulture),
            MidpointRounding.AwayFromZero));
        target.JumpDirection = suffix switch
        {
            '4' when encoded => RouteJumpDirection.StrafeLeft,
            '5' when encoded => RouteJumpDirection.StrafeRight,
            _ => RouteJumpDirection.Forward,
        };
    }

    private static string UnwrapEmbedded(string source)
    {
        string normalized = source?.Replace("\r\n", "\n", StringComparison.Ordinal)
            ?? string.Empty;
        if (normalized.StartsWith(Header, StringComparison.Ordinal))
            return normalized;
        using var reader = new StringReader(normalized);
        _ = ReadLine(reader); // embedded route display name
        _ = ReadInt(reader);
        return reader.ReadToEnd();
    }

    private static PluginNavigationPosition Position(
        double eastWest,
        double northSouth,
        double elevation) => new(
            0u,
            eastWest,
            northSouth,
            elevation,
            0f,
            IsOutdoor: true);

    internal static void Apply(NavigationSettings source, NavigationSettings target)
    {
        target.Mode = source.Mode;
        target.FollowTargetObjectId = source.FollowTargetObjectId;
        target.FollowTargetName = source.FollowTargetName;
        target.Waypoints.Clear();
        target.Waypoints.AddRange(source.Waypoints.Select(static value => value.Clone()));
    }

    private static string ReadLine(TextReader reader) =>
        reader.ReadLine() ?? throw new EndOfStreamException("Unexpected end of VTank nav data.");

    private static int ReadInt(TextReader reader) => int.Parse(
        ReadLine(reader),
        NumberStyles.Integer,
        CultureInfo.InvariantCulture);

    private static double ReadDouble(TextReader reader) => double.Parse(
        ReadLine(reader),
        NumberStyles.Float,
        CultureInfo.InvariantCulture);

    private static bool ReadBoolean(TextReader reader) => bool.Parse(ReadLine(reader));
}
