using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.App.Plugins;
using AcDream.App.World;
using AcDream.App.Rendering.Gpu;
using AcDream.Plugin.Abstractions;

namespace AcDream.App.Rendering;

/// <summary>
/// Draws the lines plugins put in the world as thin solid bars in the
/// world pass, so they sit in the scene with its depth, after the normal
/// world has been drawn.
/// </summary>
/// <remarks>
/// A plugin may send any number of lines of any length, so the draw is
/// bounded here, not by the plugin: only the part of a line within
/// <see cref="DrawRangeMeters"/> of the camera is cut into pieces, and the
/// bars of one frame never exceed the line renderer's vertex budget. When
/// the lines in range would exceed it, the lines nearest the camera are
/// drawn and the farthest are left out.
/// </remarks>
internal sealed class PluginWorldLineRenderer(
    PluginWorldLineStore store,
    DebugLineRenderer lines,
    IWorldFrameCameraSource camera,
    LiveWorldOriginState origin,
    AcDream.Core.Physics.PhysicsEngine physics)
{
    // Lines further than this from the camera are not worth the vertices.
    internal const float DrawRangeMeters = 250f;

    // A line that follows the terrain is cut into pieces about this long,
    // each end lifted to the ground under it.
    internal const float PieceMeters = 0.75f;

    // Keeps the piece arithmetic of an absurdly long line in range. Only the
    // pieces within the draw range are ever drawn, so this never costs
    // vertices; it only makes the pieces of such a line longer.
    private const int MaxPiecesPerLine = 1 << 20;

    // A bar is a box: six faces of two triangles.
    internal const int VerticesPerBar = 36;

    private readonly List<PlannedLine> _planned = [];

    public void Render(IGpuPassEncoder encoder, int width, int height)
    {
        if (!origin.IsKnown || width <= 0 || height <= 0)
            return;
        WorldCameraFrame frame = camera.Resolve();
        _planned.Clear();
        long vertices = 0;
        foreach (PluginWorldLine line in store.Lines)
        {
            Vector3 start = Project(line.Start);
            Vector3 end = Project(line.End);
            if (!float.IsFinite(start.X + start.Y + start.Z + end.X + end.Y + end.Z))
                continue;
            if (!TryPlanPieces(start, end, line.FollowTerrain, frame.Position, DrawRangeMeters, out WorldLinePieces pieces))
                continue;
            var color = new Vector3(
                (line.ColorRgb >> 16) & 255, (line.ColorRgb >> 8) & 255, line.ColorRgb & 255) / 255f;
            float thickness = float.IsFinite(line.WidthMeters)
                ? Math.Clamp(line.WidthMeters, 0.01f, 2f)
                : 0.25f;
            _planned.Add(new PlannedLine(start, end, color, thickness, line.FollowTerrain, pieces));
            vertices += (long)pieces.DrawnCount * VerticesPerBar;
        }

        lines.Begin();
        // Only when the lines do not all fit is the order worth a sort.
        if (vertices > lines.RemainingVertexBudget)
            _planned.Sort(static (a, b) => a.Pieces.DistanceSquared.CompareTo(b.Pieces.DistanceSquared));
        foreach (ref readonly PlannedLine planned in CollectionsMarshal.AsSpan(_planned))
        {
            if (planned.Pieces.DrawnCount * VerticesPerBar > lines.RemainingVertexBudget)
                break;
            Draw(in planned);
        }
        _planned.Clear();
        lines.FlushWorld(encoder, frame.ViewProjection, width, height);
    }

    /// <summary>
    /// Where a line meets the draw range around <paramref name="camera"/>,
    /// and which of its pieces are drawn. A straight line is one piece,
    /// drawn whole when any of it is in range. A line that follows the
    /// terrain is cut into pieces of about <see cref="PieceMeters"/> along
    /// its whole length, so the pieces stay put as the camera moves, and only
    /// the pieces that reach into the range are drawn.
    /// </summary>
    /// <returns>False when no part of the line is within range.</returns>
    internal static bool TryPlanPieces(
        Vector3 start,
        Vector3 end,
        bool followTerrain,
        Vector3 camera,
        float range,
        out WorldLinePieces pieces)
    {
        pieces = default;
        // Where the line enters and leaves the sphere of the range, as
        // fractions of the way from start to end. Computed in doubles so a
        // wild position cannot overflow the squares.
        double dx = end.X - start.X, dy = end.Y - start.Y, dz = end.Z - start.Z;
        double mx = start.X - camera.X, my = start.Y - camera.Y, mz = start.Z - camera.Z;
        double a = (dx * dx) + (dy * dy) + (dz * dz);
        double b = (mx * dx) + (my * dy) + (mz * dz);
        double c = (mx * mx) + (my * my) + (mz * mz) - ((double)range * range);
        double enter, leave;
        if (a < 1e-12)
        {
            if (c > 0d)
                return false;
            enter = 0d;
            leave = 1d;
        }
        else
        {
            double discriminant = (b * b) - (a * c);
            if (!double.IsFinite(discriminant) || discriminant < 0d)
                return false;
            double root = Math.Sqrt(discriminant);
            enter = (-b - root) / a;
            leave = (-b + root) / a;
            if (leave < 0d || enter > 1d)
                return false;
            enter = Math.Max(enter, 0d);
            leave = Math.Min(leave, 1d);
        }

        double nearest = a < 1e-12 ? 0d : Math.Clamp(-b / a, enter, leave);
        double nx = mx + (dx * nearest), ny = my + (dy * nearest), nz = mz + (dz * nearest);
        float distanceSquared = (float)((nx * nx) + (ny * ny) + (nz * nz));

        if (!followTerrain)
        {
            pieces = new WorldLinePieces(Count: 1, First: 0, Last: 1, distanceSquared);
            return true;
        }
        double ground = Math.Sqrt((dx * dx) + (dy * dy));
        int count = (int)Math.Clamp(Math.Ceiling(ground / PieceMeters), 1d, MaxPiecesPerLine);
        int first = (int)Math.Clamp(Math.Floor(enter * count), 0d, count - 1);
        int last = (int)Math.Clamp(Math.Ceiling(leave * count), first + 1, count);
        pieces = new WorldLinePieces(count, first, last, distanceSquared);
        return true;
    }

    private void Draw(in PlannedLine line)
    {
        WorldLinePieces pieces = line.Pieces;
        Vector3 along = line.End - line.Start;
        Vector3 previous = Ground(line.Start + (along * (float)((double)pieces.First / pieces.Count)), line.FollowTerrain);
        for (int i = pieces.First + 1; i <= pieces.Last; i++)
        {
            Vector3 next = Ground(line.Start + (along * (float)((double)i / pieces.Count)), line.FollowTerrain);
            DrawBar(previous, next, line.Color, line.Thickness);
            previous = next;
        }
    }

    private Vector3 Ground(Vector3 point, bool follow)
    {
        if (follow && physics.SampleTerrainZ(point.X, point.Y) is { } z)
            // Outdoor routes can cross raised geometry above the heightmap.
            // Preserve their recorded height; terrain only lifts a buried line.
            point.Z = MathF.Max(point.Z, z + 0.05f);
        return point;
    }

    private void DrawBar(Vector3 start, Vector3 end, Vector3 color, float thickness)
    {
        Vector3 direction = end - start;
        if (direction.LengthSquared() < 0.000001f)
            return;
        direction = Vector3.Normalize(direction);
        Vector3 side = Vector3.Cross(direction, Vector3.UnitZ);
        if (side.LengthSquared() < 0.000001f)
            side = Vector3.UnitX;
        side = Vector3.Normalize(side) * (thickness * 0.5f);
        Vector3 up = Vector3.Normalize(Vector3.Cross(side, direction)) * (thickness * 0.5f);
        Span<Vector3> vertices = stackalloc Vector3[8]
        {
            start - side - up, start + side - up, start + side + up, start - side + up,
            end - side - up, end + side - up, end + side + up, end - side + up,
        };
        Quad(vertices[0], vertices[1], vertices[2], vertices[3], color);
        Quad(vertices[4], vertices[5], vertices[6], vertices[7], color);
        for (int i = 0; i < 4; i++)
        {
            int next = (i + 1) % 4;
            Quad(vertices[i], vertices[next], vertices[next + 4], vertices[i + 4], color);
        }
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
    {
        lines.AddTriangle(a, b, c, color);
        lines.AddTriangle(a, c, d, color);
    }

    // Landblock coordinates to the live world's origin-relative frame: a
    // landblock is 192 m across, a position's cell offsets are in 240ths.
    private Vector3 Project(PluginNavigationPosition p) => new(
        (float)(p.EastWest * 240d + (127 - origin.CenterX) * 192d + 84d),
        (float)(p.NorthSouth * 240d + (127 - origin.CenterY) * 192d + 84d),
        (float)(p.Elevation * 240d));

    private readonly record struct PlannedLine(
        Vector3 Start,
        Vector3 End,
        Vector3 Color,
        float Thickness,
        bool FollowTerrain,
        WorldLinePieces Pieces);
}

/// <summary>
/// The pieces of one world line that are drawn: the line is cut into
/// <paramref name="Count"/> equal pieces and pieces <paramref name="First"/>
/// up to, not including, <paramref name="Last"/> are drawn.
/// <paramref name="DistanceSquared"/> is the squared distance in meters from
/// the camera to the nearest point of the line in range.
/// </summary>
internal readonly record struct WorldLinePieces(int Count, int First, int Last, float DistanceSquared)
{
    public int DrawnCount => Last - First;
}
