using System.Numerics;
using System.Runtime.CompilerServices;
using AcDream.Core.Physics;

namespace AcDream.Core.Navigation;

/// <summary>The shape a navigation grid is built for.</summary>
public readonly record struct NavBody(
    float Radius,
    float Height,
    float StepUpHeight,
    float StepDownHeight)
{
    /// <summary>The player's body as the movement controller sweeps it: spheres of 0.48 reaching 1.835 high.</summary>
    public static NavBody Player(float stepUpHeight, float stepDownHeight) =>
        new(0.48f, 1.835f, stepUpHeight, stepDownHeight);
}

public readonly record struct NavTriangle(Vector3 A, Vector3 B, Vector3 C);

/// <summary>
/// A cylinder or sphere a body meets, as the upright extent it fills. A body stands
/// anywhere on a cylinder's flat top; a dome is a sphere, whose top curves away, so a
/// body stands only over the part of it that is no steeper than a floor.
/// </summary>
public readonly record struct NavCylinder(Vector3 Base, float Radius, float Height, bool Dome = false);

/// <summary>
/// The collision of one object, as the triangles and upright extents a grid builds its floors
/// and walls from, for finding the part of a grid that stands on it.
/// </summary>
public sealed record NavSurfaces(IReadOnlyList<NavTriangle> Triangles, IReadOnlyList<NavCylinder> Cylinders);

/// <summary>One landblock's terrain, and the world position of its south-west corner.</summary>
public readonly record struct NavTerrain(TerrainSurface Surface, float OriginX, float OriginY);

/// <summary>
/// The fixed collision geometry of a square region of the world, copied out of
/// the physics world so a grid can be built from it on another thread: the
/// terrain, interior cell polygons, building shells and placed objects of every
/// resident landblock the region overlaps, and the objects the server placed
/// there that a body stands on, such as the rocks a jump puzzle is built from.
/// Doors, creatures and players are left out; a walk steers around those where
/// they stand now.
/// </summary>
public sealed class NavGeometry
{
    public const float LandblockSize = 192f;

    public NavGeometry(
        float originX,
        float originY,
        float size,
        IReadOnlyList<NavTerrain> terrains,
        IReadOnlyList<NavTriangle> cellTriangles,
        IReadOnlyList<NavTriangle> objectTriangles,
        IReadOnlyList<NavCylinder> cylinders,
        IReadOnlyList<NavTriangle>? shellTriangles = null)
    {
        if (!(size > 0f) || !float.IsFinite(size))
            throw new ArgumentOutOfRangeException(nameof(size));
        ArgumentNullException.ThrowIfNull(terrains);
        ArgumentNullException.ThrowIfNull(cellTriangles);
        ArgumentNullException.ThrowIfNull(objectTriangles);
        ArgumentNullException.ThrowIfNull(cylinders);
        OriginX = originX;
        OriginY = originY;
        Size = size;
        Terrains = terrains;
        CellTriangles = cellTriangles;
        ObjectTriangles = objectTriangles;
        Cylinders = cylinders;
        ShellTriangles = shellTriangles ?? [];
    }

    /// <summary>The world position of the region's south-west corner.</summary>
    public float OriginX { get; }

    public float OriginY { get; }

    /// <summary>The length of the region's sides in meters.</summary>
    public float Size { get; }

    public IReadOnlyList<NavTerrain> Terrains { get; }

    /// <summary>Interior cell polygons. Terrain is left out of every column where it does not stand above them.</summary>
    public IReadOnlyList<NavTriangle> CellTriangles { get; }

    /// <summary>Placed objects, and the objects the server placed that a body stands on.</summary>
    public IReadOnlyList<NavTriangle> ObjectTriangles { get; }

    /// <summary>
    /// The outside of buildings. A building's shell bounds the ground around it and
    /// carries its roof, but not the rooms inside it, which its cells hold.
    /// </summary>
    public IReadOnlyList<NavTriangle> ShellTriangles { get; }

    public IReadOnlyList<NavCylinder> Cylinders { get; }

    /// <summary>The resident landblocks the region overlaps.</summary>
    public IReadOnlyList<uint> LandblockIds { get; init; } = [];

    /// <summary>
    /// What the objects a body meets here came to: their ids and where they stood, so a
    /// grid is known to be out of date once one arrives, leaves or moves. Every object
    /// the caller's rule names counts, whether a landblock owns it or the server placed
    /// it, so that this and <see cref="FingerprintObjects"/> always weigh the same
    /// objects.
    /// </summary>
    public ulong ObjectFingerprint { get; init; }

    /// <summary>
    /// The objects the server placed whose collision this geometry holds. A route has
    /// no need to keep out of them: they are already floors and walls in the grid.
    /// </summary>
    public IReadOnlySet<uint> ObjectIds { get; init; } = new HashSet<uint>();

    /// <summary>
    /// Copies one resident landblock's fixed collision geometry, or returns null
    /// when the landblock has no collision in the physics world.
    /// </summary>
    public static NavGeometry? CaptureLandblock(PhysicsEngine engine, uint landblockId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.TryGetLandblockCollision(landblockId, out _, out _, out Vector3 offset)
            ? Capture(engine, offset.X, offset.Y, LandblockSize)
            : null;
    }

    /// <summary>
    /// Copies the fixed collision geometry of a square region, or returns null
    /// when no resident landblock overlaps it. Call it on the thread that owns
    /// the physics world.
    /// </summary>
    public static NavGeometry? Capture(
        PhysicsEngine engine,
        float originX,
        float originY,
        float size,
        Func<uint, bool>? standsOn = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        IReadOnlyList<uint> landblocks = OverlappingLandblocks(engine, originX, originY, size);
        return landblocks.Count == 0
            ? null
            : Capture(engine, originX, originY, size, landblocks, includeTerrain: true, standsOn);
    }

    /// <summary>
    /// Copies the fixed collision geometry of a sealed dungeon over a square
    /// region: the cells and placed objects of the one landblock that holds the
    /// dungeon, without terrain, since nothing in a sealed dungeon stands on it.
    /// Returns null when that landblock is not resident. Call it on the thread
    /// that owns the physics world.
    /// </summary>
    public static NavGeometry? CaptureDungeon(
        PhysicsEngine engine,
        uint landblockId,
        float originX,
        float originY,
        float size,
        Func<uint, bool>? standsOn = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        uint canonical = (landblockId & 0xFFFF0000u) | 0xFFFFu;
        return engine.TryGetLandblockCollision(canonical, out _, out _, out _)
            ? Capture(engine, originX, originY, size, [canonical], includeTerrain: false, standsOn)
            : null;
    }

    private static NavGeometry Capture(
        PhysicsEngine engine,
        float originX,
        float originY,
        float size,
        IReadOnlyList<uint> landblocks,
        bool includeTerrain,
        Func<uint, bool>? standsOn = null)
    {
        var terrains = new List<NavTerrain>();
        var cellTriangles = new List<NavTriangle>();
        var objectTriangles = new List<NavTriangle>();
        var shellTriangles = new List<NavTriangle>();
        var owners = new HashSet<uint>();
        foreach (uint landblockId in landblocks)
        {
            engine.TryGetLandblockCollision(
                landblockId,
                out TerrainSurface terrain,
                out IReadOnlyList<CellSurface> cells,
                out Vector3 offset);
            if (includeTerrain)
                terrains.Add(new NavTerrain(terrain, offset.X, offset.Y));
            foreach (CellSurface cell in cells)
            {
                foreach ((Vector3 a, Vector3 b, Vector3 c) in cell.Triangles)
                    cellTriangles.Add(new NavTriangle(a, b, c));
            }
            AddBuildingShells(engine.DataCache, landblockId, shellTriangles);
            owners.UnionWith(engine.ShadowObjects.CaptureStaticOwnersForLandblock(landblockId));
        }

        var cylinders = new List<NavCylinder>();
        ulong fingerprint = 0uL;
        int counted = 0;
        var stoodOnIds = new HashSet<uint>();
        var entries = new List<ShadowEntry>();
        engine.ShadowObjects.CaptureEntries(entries);
        foreach (ShadowEntry entry in entries)
        {
            if (!Meets(entry))
                continue;
            bool named = standsOn?.Invoke(entry.EntityId) == true
                && Within(entry.Position, originX, originY, size);
            if (named)
            {
                fingerprint ^= Fingerprint(entry);
                counted++;
            }
            if (!owners.Contains(entry.EntityId))
            {
                if (!named)
                    continue;
                stoodOnIds.Add(entry.EntityId);
            }
            AddCollision(engine.DataCache, entry, cylinders, objectTriangles);
        }

        return new NavGeometry(
            originX,
            originY,
            size,
            terrains,
            cellTriangles,
            objectTriangles,
            cylinders,
            shellTriangles)
        {
            LandblockIds = landblocks,
            ObjectFingerprint = Fold(fingerprint, counted),
            ObjectIds = stoodOnIds,
        };
    }

    /// <summary>
    /// The collision of the object the physics world knows as <paramref name="entityId"/>, every
    /// part of it that a body meets, or null when it has none.
    /// </summary>
    public static NavSurfaces? SurfacesOf(PhysicsEngine engine, uint entityId)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var entries = new List<ShadowEntry>();
        engine.ShadowObjects.CaptureEntries(entityId, entries);
        var triangles = new List<NavTriangle>();
        var cylinders = new List<NavCylinder>();
        foreach (ShadowEntry entry in entries)
        {
            if (Meets(entry))
                AddCollision(engine.DataCache, entry, cylinders, triangles);
        }
        return triangles.Count == 0 && cylinders.Count == 0 ? null : new NavSurfaces(triangles, cylinders);
    }

    private static void AddCollision(
        PhysicsDataCache? cache,
        ShadowEntry entry,
        List<NavCylinder> cylinders,
        List<NavTriangle> triangles)
    {
        switch (entry.CollisionType)
        {
            case ShadowCollisionType.Cylinder:
                cylinders.Add(new NavCylinder(
                    entry.Position,
                    entry.Radius,
                    entry.CylHeight > 0f ? entry.CylHeight : entry.Radius * 2f));
                break;
            case ShadowCollisionType.Sphere:
                cylinders.Add(new NavCylinder(
                    entry.Position - new Vector3(0f, 0f, entry.Radius),
                    entry.Radius,
                    entry.Radius * 2f,
                    Dome: true));
                break;
            default:
                AddObjectTriangles(
                    cache?.GetGfxObj(entry.GfxObjId),
                    Matrix4x4.CreateScale(entry.Scale)
                        * Matrix4x4.CreateFromQuaternion(entry.Rotation)
                        * Matrix4x4.CreateTranslation(entry.Position),
                    triangles);
                break;
        }
    }

    /// <summary>
    /// Whether a body meets an object where it stands: not one that is ethereal, which
    /// a body walks through, and not a missile in flight. An arrow, a thrown blade or a
    /// spell's bolt stands nowhere: it is on its way and gone in a moment, so it is
    /// neither floor nor wall, and it moves every frame, which would leave a grid built
    /// with one in it out of date the moment it was built.
    /// </summary>
    private static bool Meets(ShadowEntry entry)
    {
        var state = (PhysicsStateFlags)entry.State;
        return !state.HasFlag(PhysicsStateFlags.Ethereal) && !state.HasFlag(PhysicsStateFlags.Missile);
    }

    /// <summary>How much of an object outside a region still counts as standing in it.</summary>
    private const float RegionSlack = 4f;

    /// <summary>Whether a point stands in a region, with room to spare for an object's own width.</summary>
    private static bool Within(Vector3 point, float originX, float originY, float size) =>
        point.X >= originX - RegionSlack
        && point.X <= originX + size + RegionSlack
        && point.Y >= originY - RegionSlack
        && point.Y <= originY + size + RegionSlack;

    /// <summary>
    /// What the objects the server placed in a region come to now, to compare against
    /// what a grid was built from. Call it on the thread that owns the physics world.
    /// </summary>
    public static ulong FingerprintObjects(
        PhysicsEngine engine,
        float originX,
        float originY,
        float size,
        Func<uint, bool>? standsOn)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (standsOn is null)
            return 0uL;
        ulong fingerprint = 0uL;
        int counted = 0;
        var entries = new List<ShadowEntry>();
        engine.ShadowObjects.CaptureEntries(entries);
        foreach (ShadowEntry entry in entries)
        {
            if (!Meets(entry)
                || !Within(entry.Position, originX, originY, size)
                || !standsOn(entry.EntityId))
            {
                continue;
            }
            fingerprint ^= Fingerprint(entry);
            counted++;
        }
        return Fold(fingerprint, counted);
    }

    /// <summary>Folds how many objects were counted into their fingerprint.</summary>
    private static ulong Fold(ulong fingerprint, int counted) =>
        counted == 0 ? 0uL : fingerprint ^ ((ulong)counted * 0x9E3779B97F4A7C15uL);

    /// <summary>
    /// What one server object comes to: its id and where it stands, to a tenth of a
    /// meter, so the objects a region holds can be compared without keeping them.
    /// </summary>
    private static ulong Fingerprint(ShadowEntry entry)
    {
        ulong value = entry.EntityId;
        value = (value * 1000003uL) ^ (ulong)(long)MathF.Round(entry.Position.X * 10f);
        value = (value * 1000003uL) ^ (ulong)(long)MathF.Round(entry.Position.Y * 10f);
        value = (value * 1000003uL) ^ (ulong)(long)MathF.Round(entry.Position.Z * 10f);
        return value;
    }

    /// <summary>
    /// The horizontal footprint of one part of an object's collision, as the point at
    /// its middle and the radius it fills: a cylinder or sphere as it was registered,
    /// and a part with a collision model of its own as that model's bounding sphere,
    /// placed, turned and scaled with the part, since a part's own position can lie
    /// well off the middle of its model.
    /// </summary>
    public static NavAvoidance FootprintOf(ShadowEntry entry, PhysicsDataCache? cache)
    {
        if (entry.CollisionType != ShadowCollisionType.BSP || cache?.GetGfxObj(entry.GfxObjId) is not { } model)
            return new NavAvoidance(entry.Position, entry.Radius);
        FlatCollisionSphere sphere = model.FlatPhysicsBsp is { RootIndex: >= 0 } flat
            ? flat.Nodes[flat.RootIndex].BoundingSphere
            : new FlatCollisionSphere(model.BoundingSphere?.Origin ?? Vector3.Zero, model.BoundingSphere?.Radius ?? entry.Radius);
        float scale = entry.Scale > 0f ? entry.Scale : 1f;
        Quaternion rotation = entry.Rotation == default ? Quaternion.Identity : entry.Rotation;
        return new NavAvoidance(entry.Position + Vector3.Transform(sphere.Origin * scale, rotation), sphere.Radius * scale);
    }

    /// <summary>
    /// The resident landblocks a square region overlaps, by their terrain's square
    /// or by the extent of their interior cells, which in a dungeon can lie far
    /// outside that square.
    /// </summary>
    public static IReadOnlyList<uint> OverlappingLandblocks(PhysicsEngine engine, float originX, float originY, float size)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var overlapping = new List<uint>();
        foreach (uint landblockId in engine.LandblockIds)
        {
            if (!engine.TryGetLandblockCollision(landblockId, out _, out IReadOnlyList<CellSurface> cells, out Vector3 offset))
                continue;
            bool terrain = offset.X < originX + size
                && offset.X + LandblockSize > originX
                && offset.Y < originY + size
                && offset.Y + LandblockSize > originY;
            if (terrain || ExtentOf(cells).Overlaps(originX, originY, size))
                overlapping.Add(landblockId);
        }
        return overlapping;
    }

    /// <summary>
    /// The horizontal extent of a resident landblock's interior cells, or false
    /// when the physics world holds none for it.
    /// </summary>
    public static bool TryMeasureCells(PhysicsEngine engine, uint landblockId, out Vector2 minimum, out Vector2 maximum)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.TryGetLandblockCollision(landblockId, out _, out IReadOnlyList<CellSurface> cells, out _))
        {
            minimum = new Vector2(float.PositiveInfinity);
            maximum = new Vector2(float.NegativeInfinity);
            return false;
        }
        CellExtent extent = ExtentOf(cells);
        minimum = extent.Minimum;
        maximum = extent.Maximum;
        return !extent.IsEmpty;
    }

    /// <summary>A landblock's cell list is replaced, never edited, so each list's extent is measured once.</summary>
    private static readonly ConditionalWeakTable<IReadOnlyList<CellSurface>, CellExtent> CellExtents = new();

    private static CellExtent ExtentOf(IReadOnlyList<CellSurface> cells) =>
        CellExtents.GetValue(cells, static list => new CellExtent(list));

    private sealed class CellExtent
    {
        public CellExtent(IReadOnlyList<CellSurface> cells)
        {
            var low = new Vector2(float.PositiveInfinity);
            var high = new Vector2(float.NegativeInfinity);
            foreach (CellSurface cell in cells)
            {
                foreach ((Vector3 a, Vector3 b, Vector3 c) in cell.Triangles)
                {
                    low = Vector2.Min(low, Vector2.Min(Flat(a), Vector2.Min(Flat(b), Flat(c))));
                    high = Vector2.Max(high, Vector2.Max(Flat(a), Vector2.Max(Flat(b), Flat(c))));
                }
            }
            Minimum = low;
            Maximum = high;
        }

        public Vector2 Minimum { get; }

        public Vector2 Maximum { get; }

        public bool IsEmpty => Minimum.X > Maximum.X;

        public bool Overlaps(float originX, float originY, float size) =>
            !IsEmpty
            && Minimum.X < originX + size
            && Maximum.X > originX
            && Minimum.Y < originY + size
            && Maximum.Y > originY;
    }

    private static Vector2 Flat(Vector3 point) => new(point.X, point.Y);

    private static void AddBuildingShells(PhysicsDataCache? cache, uint landblockId, List<NavTriangle> triangles)
    {
        if (cache is null)
            return;
        uint prefix = landblockId & 0xFFFF0000u;
        foreach (uint landcellId in cache.BuildingIds)
        {
            if ((landcellId & 0xFFFF0000u) != prefix
                || cache.GetBuilding(landcellId) is not { ModelId: not 0u } building)
            {
                continue;
            }
            AddObjectTriangles(cache.GetGfxObj(building.ModelId), building.WorldTransform, triangles);
        }
    }

    private static void AddObjectTriangles(
        GfxObjPhysics? physics,
        Matrix4x4 placement,
        List<NavTriangle> triangles)
    {
        if (physics?.FlatPhysicsBsp?.PolygonTable is { } table)
        {
            foreach (FlatCollisionPolygon polygon in table.Polygons)
            {
                FlatIndexRange range = polygon.VertexRange;
                if (range.Count < 3)
                    continue;
                Vector3 first = Vector3.Transform(table.Vertices[range.Start], placement);
                Vector3 previous = Vector3.Transform(table.Vertices[range.Start + 1], placement);
                for (int offset = 2; offset < range.Count; offset++)
                {
                    Vector3 current = Vector3.Transform(table.Vertices[range.Start + offset], placement);
                    triangles.Add(new NavTriangle(first, previous, current));
                    previous = current;
                }
            }
            return;
        }

        if (physics?.PhysicsPolygons is not { } polygons || physics.Vertices is not { } vertexArray)
            return;
        var corners = new List<Vector3>(8);
        foreach (var polygon in polygons.Values)
        {
            corners.Clear();
            foreach (var vertexId in polygon.VertexIds)
            {
                if (!vertexArray.Vertices.TryGetValue((ushort)vertexId, out var vertex))
                {
                    corners.Clear();
                    break;
                }
                corners.Add(Vector3.Transform(vertex.Origin, placement));
            }
            for (int index = 2; index < corners.Count; index++)
                triangles.Add(new NavTriangle(corners[0], corners[index - 1], corners[index]));
        }
    }
}
