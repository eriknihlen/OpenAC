using System.Numerics;
using AcDream.Core.Physics;

namespace AcDream.Core.Ui;

public enum RadarBehavior : byte
{
    Undef = 0,
    ShowNever = 1,
    ShowMovement = 2,
    ShowAttacking = 3,
    ShowAlways = 4,
}

public enum RadarBlipShape
{
    Undef = 0,
    Circle = 1,
    Box = 2,
    X = 3,
    Plus = 4,
    Triangle = 5,
    InvertedTriangle = 6,
    XBox = 7,

    Default = Plus,
    AllegianceMember = Box,
    FellowshipLeader = Triangle,
    Fellowship = InvertedTriangle,
    Threat = X,
    ThreatAllegiance = XBox,
}

public enum RadarCompassPoint
{
    North,
    East,
    South,
    West,
}

public readonly record struct RadarPixelOffset(int X, int Y);

public readonly record struct RadarProjection(Vector2 Pixel, float RgbMultiplier);

public static class RetailRadar
{
    public const float OutdoorRangeMeters = 75f;
    public const float IndoorRangeMeters = 25f;
    public const float AltitudeDimThresholdMeters = 5f;
    public const float AltitudeDimMultiplier = 0.65f;
    public const float UpdateIntervalSeconds = 0.025f;

    private static readonly RadarPixelOffset[] PointPixels = [new(0, 0)];
    private static readonly RadarPixelOffset[] EdgePixels = [new(0, -1), new(0, 1), new(-1, 0), new(1, 0)];
    private static readonly RadarPixelOffset[] CornerPixels = [new(1, 1), new(-1, -1), new(-1, 1), new(1, -1)];
    private static readonly RadarPixelOffset[] BoxPixels = [.. EdgePixels, .. CornerPixels];
    private static readonly RadarPixelOffset[] XPixels = [new(0, 0), .. CornerPixels];
    private static readonly RadarPixelOffset[] PlusPixels = [new(0, 0), .. EdgePixels];
    private static readonly RadarPixelOffset[] TrianglePixels = [new(0, 0), new(-1, 1), new(0, 1), new(1, 1)];
    private static readonly RadarPixelOffset[] InvertedTrianglePixels = [new(0, 0), new(-1, -1), new(0, -1), new(1, -1)];
    private static readonly RadarPixelOffset[] XBoxPixels =
    [
        .. EdgePixels,
        .. CornerPixels,
        new(-2, -2),
        new(2, -2),
        new(-2, 2),
        new(2, 2),
    ];

    private static readonly RadarPixelOffset[] PlayerMarkerPixelArray =
    [
        new(0, 0),
        .. EdgePixels,
        new(-2, 0),
        new(2, 0),
        new(0, -2),
        new(0, 2),
    ];

    private static readonly RadarPixelOffset[] SelectionPixelArray = BuildSelectionPixels();

    public static bool IsShowable(RadarBehavior behavior, bool hasPhysicsObject)
        => hasPhysicsObject && behavior is
            RadarBehavior.ShowMovement or RadarBehavior.ShowAttacking or RadarBehavior.ShowAlways;

    public static RadarBlipShape GetBlipShape(
        RadarObjectTraits target,
        RadarRelationshipTraits relationship = default)
    {
        if (!target.IsValid)
            return RadarBlipShape.Undef;

        if (relationship.IsFellowshipLeader)
            return RadarBlipShape.FellowshipLeader;

        if (relationship.IsFellowshipMember)
            return RadarBlipShape.Fellowship;

        if (relationship.IsAllegianceMember)
            return RadarBlipShape.AllegianceMember;

        if ((target.IsPlayerKiller && relationship.PlayerIsPlayerKiller) ||
            (target.IsPkLite && relationship.PlayerIsPkLite))
        {
            return RadarBlipShape.Threat;
        }

        return RadarBlipShape.Default;
    }

    public static ReadOnlySpan<RadarPixelOffset> GetBlipPixels(RadarBlipShape shape)
        => shape switch
        {
            RadarBlipShape.Circle => PointPixels,
            RadarBlipShape.Box => BoxPixels,
            RadarBlipShape.X => XPixels,
            RadarBlipShape.Plus => PlusPixels,
            RadarBlipShape.Triangle => TrianglePixels,
            RadarBlipShape.InvertedTriangle => InvertedTrianglePixels,
            RadarBlipShape.XBox => XBoxPixels,
            _ => [],
        };

    public static ReadOnlySpan<RadarPixelOffset> PlayerMarkerPixels => PlayerMarkerPixelArray;

    public static ReadOnlySpan<RadarPixelOffset> SelectionPixels => SelectionPixelArray;

    public static float GetRangeMeters(bool isOutside)
        => isOutside ? OutdoorRangeMeters : IndoorRangeMeters;

    public static bool TryProject(
        Vector3 playerSpaceMeters,
        Vector2 centerPixels,
        int radarRadiusPixels,
        float radarRangeMeters,
        out RadarProjection projection)
    {
        float inclusionRadius = radarRangeMeters - 1f;
        float horizontalDistanceSquared =
            (playerSpaceMeters.X * playerSpaceMeters.X) +
            (playerSpaceMeters.Y * playerSpaceMeters.Y);

        if (horizontalDistanceSquared >= inclusionRadius * inclusionRadius)
        {
            projection = default;
            return false;
        }

        float scale = radarRadiusPixels / radarRangeMeters;
        projection = new RadarProjection(
            new Vector2(
                (int)(centerPixels.X + (playerSpaceMeters.X * scale)),
                (int)(centerPixels.Y - (playerSpaceMeters.Y * scale))),
            GetAltitudeRgbMultiplier(playerSpaceMeters.Z));
        return true;
    }

    public static float GetAltitudeRgbMultiplier(float playerSpaceZMeters)
        => MathF.Abs(playerSpaceZMeters) < AltitudeDimThresholdMeters
            ? 1f
            : AltitudeDimMultiplier;

    public static Vector2 GetCompassTokenTopLeft(
        float playerHeadingDegrees,
        RadarCompassPoint point,
        Vector2 radarCenterPixels,
        float tokenMagnitudePixels,
        Vector2 tokenSizePixels)
    {
        float headingRadians = playerHeadingDegrees * 0.0174532924f;
        double angle = headingRadians + (point switch
        {
            RadarCompassPoint.North => Math.PI,
            RadarCompassPoint.East => (double)1.57079637f,
            RadarCompassPoint.South => 0d,
            RadarCompassPoint.West => (double)4.71238899f,
            _ => 0d,
        });

        float offsetX = (float)(Math.Sin(angle) * tokenMagnitudePixels);
        float tokenCenterX = (float)(offsetX + radarCenterPixels.X);
        float tokenCenterY = (float)((Math.Cos(angle) * tokenMagnitudePixels) + radarCenterPixels.Y);

        return new Vector2(
            (int)(tokenCenterX - (tokenSizePixels.X * 0.5f)),
            (int)(tokenCenterY - (tokenSizePixels.Y * 0.5f)));
    }

    private static RadarPixelOffset[] BuildSelectionPixels()
    {
        var pixels = new RadarPixelOffset[20];
        int index = 0;
        for (int x = -2; x <= 2; x++)
        {
            pixels[index++] = new RadarPixelOffset(x, 3);
            pixels[index++] = new RadarPixelOffset(x, -3);
        }

        for (int y = -2; y <= 2; y++)
        {
            pixels[index++] = new RadarPixelOffset(3, y);
            pixels[index++] = new RadarPixelOffset(-3, y);
        }

        return pixels;
    }
}

public readonly record struct RadarCoordinates(double X, double Y)
{
    public string XText => FormatAxis(X, "W", "E");
    public string YText => FormatAxis(Y, "S", "N");
    public string CombinedText => $"{YText},{XText}";

    public static bool TryFromCell(uint cellId, out RadarCoordinates coordinates)
    {
        if (!LandDefs.GidToLcoord(cellId, out int lx, out int ly))
        {
            coordinates = default;
            return false;
        }

        coordinates = new RadarCoordinates(
            ((lx - 1024) * 0.1) + 0.5,
            ((ly - 1024) * 0.1) + 0.5);
        return true;
    }

    private static string FormatAxis(double value, string negativeSuffix, string positiveSuffix)
    {
        string suffix = value < 0d ? negativeSuffix : value > 0d ? positiveSuffix : string.Empty;
        return $"{Math.Abs(value).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}{suffix}";
    }
}
