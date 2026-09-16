using System.Numerics;
using AcDream.Content;
using AcDream.DrakBot.Remote;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;

namespace AcDream.App.Plugins;

/// <summary>
/// Reads a dungeon's floors and walls out of the game's data files for
/// DrakBot Remote's floor plans: every environment cell of the landblock,
/// its collision polygons placed by the cell's frame, in the map frame
/// the status document's wx and wy use. Called on the plugin tick, so the
/// data files are read from the thread that owns them.
/// </summary>
internal static class RemoteDungeonGeometryLoader
{
    /// <summary>
    /// Metres from the world's corner to map (0, 0): landblock 0x7F7F at
    /// 84 m in, as the automation surface projects positions
    /// ((block - 127) * 192 + local - 84).
    /// </summary>
    public const double MapOriginMetres = 127d * 192d + 84d;

    /// <summary>A face this upright (its normal's z) is level ground.</summary>
    public const float FloorNormalZ = 0.98f;
    /// <summary>A face this upright at least is a slope you walk; below it, a wall.</summary>
    public const float RampNormalZ = 0.5f;
    /// <summary>Environment cells run from 0x0100 to 0xFFFD within a landblock.</summary>
    private const uint MaxCells = 0xFFFDu - 0x0100u + 1u;

    public static RemoteDungeonGeometry? Load(IDatReaderWriter? dats, uint landblockId)
    {
        if (dats is null)
            return null;
        uint landblock = landblockId > 0xFFFFu ? landblockId & 0xFFFF0000u : landblockId << 16;
        if (landblock == 0u
            || !dats.Cell.TryGet<LandBlockInfo>(landblock | 0xFFFEu, out LandBlockInfo? info)
            || info.NumCells == 0)
        {
            return null;
        }
        var origin = new Vector3(
            (float)(((landblock >> 24) & 0xFFu) * 192d - MapOriginMetres),
            (float)(((landblock >> 16) & 0xFFu) * 192d - MapOriginMetres),
            0f);
        var environments = new Dictionary<uint, DatEnvironment?>();
        var polygons = new List<RemoteDungeonPolygon>();
        uint count = Math.Min(info.NumCells, MaxCells);
        for (uint offset = 0; offset < count; offset++)
        {
            uint cellId = landblock | (0x0100u + offset);
            if (!dats.Cell.TryGet<EnvCell>(cellId, out EnvCell? cell) || cell.EnvironmentId == 0)
                continue;
            uint environmentId = 0x0D000000u | cell.EnvironmentId;
            if (!environments.TryGetValue(environmentId, out DatEnvironment? environment))
            {
                environment = dats.Portal.TryGet<DatEnvironment>(environmentId, out DatEnvironment? read) ? read : null;
                environments[environmentId] = environment;
            }
            if (environment is null || !environment.Cells.TryGetValue(cell.CellStructure, out CellStruct? structure))
                continue;
            Append(polygons, structure, Matrix4x4.CreateFromQuaternion(cell.Position.Orientation), cell.Position.Origin + origin);
        }
        return polygons.Count == 0 ? null : new RemoteDungeonGeometry(landblock, polygons);
    }

    /// <summary>
    /// The cell's collision polygons, placed. The collision set is what the
    /// character walks on and into; the drawn set would bring the doorway
    /// polygons with it, which are openings, not walls.
    /// </summary>
    private static void Append(List<RemoteDungeonPolygon> polygons, CellStruct structure, in Matrix4x4 rotation, Vector3 origin)
    {
        Dictionary<ushort, Polygon> source = structure.PhysicsPolygons.Count > 0 ? structure.PhysicsPolygons : structure.Polygons;
        bool drawn = ReferenceEquals(source, structure.Polygons);
        Dictionary<ushort, SWVertex> vertices = structure.VertexArray.Vertices;
        foreach ((ushort id, Polygon polygon) in source)
        {
            if (polygon.VertexIds.Count < 3 || (drawn && structure.Portals.Contains(id)))
                continue;
            var local = new Vector3[polygon.VertexIds.Count];
            bool complete = true;
            for (int index = 0; index < local.Length; index++)
            {
                if (!vertices.TryGetValue((ushort)polygon.VertexIds[index], out SWVertex? vertex))
                {
                    complete = false;
                    break;
                }
                local[index] = vertex.Origin;
            }
            if (!complete)
                continue;
            Vector3 normal = Vector3.TransformNormal(Newell(local), rotation);
            float length = normal.Length();
            if (length <= 0f)
                continue;
            float up = normal.Z / length;
            RemoteDungeonSurface kind;
            if (up >= FloorNormalZ)
                kind = RemoteDungeonSurface.Floor;
            else if (up >= RampNormalZ)
                kind = RemoteDungeonSurface.Ramp;
            else if (up > -RampNormalZ)
                kind = RemoteDungeonSurface.Wall;
            else
                continue;   // a ceiling
            var points = new Vector2[local.Length];
            float zMin = float.PositiveInfinity, zMax = float.NegativeInfinity;
            for (int index = 0; index < local.Length; index++)
            {
                Vector3 world = Vector3.Transform(local[index], rotation) + origin;
                points[index] = new Vector2(world.X, world.Y);
                zMin = Math.Min(zMin, world.Z);
                zMax = Math.Max(zMax, world.Z);
            }
            polygons.Add(new RemoteDungeonPolygon(kind, points, zMin, zMax));
        }
    }

    /// <summary>The polygon's normal by Newell's method, unnormalized; robust to a bent quad.</summary>
    private static Vector3 Newell(Vector3[] polygon)
    {
        Vector3 normal = Vector3.Zero;
        for (int index = 0; index < polygon.Length; index++)
        {
            Vector3 current = polygon[index];
            Vector3 next = polygon[(index + 1) % polygon.Length];
            normal.X += (current.Y - next.Y) * (current.Z + next.Z);
            normal.Y += (current.Z - next.Z) * (current.X + next.X);
            normal.Z += (current.X - next.X) * (current.Y + next.Y);
        }
        return normal;
    }
}
