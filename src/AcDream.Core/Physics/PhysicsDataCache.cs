using System.Collections.Concurrent;
using System.Numerics;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Plane = System.Numerics.Plane;
using UcgEnvCell = AcDream.Core.World.Cells.EnvCell;
using UcgCellGraph = AcDream.Core.World.Cells.CellGraph;
using PreparedCellGraphLandblock = AcDream.Core.World.Cells.PreparedCellGraphLandblock;

namespace AcDream.Core.Physics;

public sealed class PhysicsDataCache
{
    private readonly bool _requirePreparedCollision;
    private PhysicsDataCache? _readFallback;
    private readonly CollisionWorldStateSlot _collisionWorld;
    private readonly ConcurrentDictionary<uint, GfxObjPhysics> _gfxObj = new();
    private readonly ConcurrentDictionary<uint, GfxObjVisualBounds> _visualBounds = new();
    private readonly ConcurrentDictionary<uint, SetupPhysics> _setup = new();
    private ConcurrentDictionary<uint, CellPhysics> _cellStruct =>
        _collisionWorld.Current.CellStruct;
    private readonly ConcurrentDictionary<uint, FlatGfxObjCollisionAsset>
        _flatGfxObj = new();
    private readonly ConcurrentDictionary<uint, FlatSetupCollision>
        _flatSetup = new();
    private ConcurrentDictionary<uint, FlatCellStructureCollisionAsset>
        _flatCellStruct => _collisionWorld.Current.FlatCellStruct;
    private ConcurrentDictionary<uint, FlatEnvCellTopology>
        _flatEnvCell => _collisionWorld.Current.FlatEnvCell;

    public PhysicsDataCache()
        : this(requirePreparedCollision: false)
    {
    }

    private PhysicsDataCache(bool requirePreparedCollision)
        : this(requirePreparedCollision, new CollisionWorldStateSlot())
    {
    }

    private PhysicsDataCache(
        bool requirePreparedCollision,
        CollisionWorldStateSlot collisionWorld)
    {
        _requirePreparedCollision = requirePreparedCollision;
        _collisionWorld = collisionWorld
            ?? throw new ArgumentNullException(nameof(collisionWorld));
        CellGraph = new UcgCellGraph(_collisionWorld);
        if (!requirePreparedCollision
            && PhysicsDiagnostics.CollisionShadowSampleEvery > 0)
        {
            CollisionShadow = new CollisionShadowVerifier(
                PhysicsDiagnostics.CollisionShadowSampleEvery,
                PhysicsDiagnostics.CollisionShadowArtifactDirectory);
        }
    }

    public static PhysicsDataCache CreateProduction()
    {
        var cache = new PhysicsDataCache(requirePreparedCollision: true)
        {
            CollisionTraversalMode = CollisionTraversalMode.Flat,
        };
        return cache;
    }

    internal static PhysicsDataCache CreateProduction(
        CollisionWorldStateSlot collisionWorld)
    {
        var cache = new PhysicsDataCache(
            requirePreparedCollision: true,
            collisionWorld)
        {
            CollisionTraversalMode = CollisionTraversalMode.Flat,
        };
        return cache;
    }

    internal CollisionShadowVerifier? CollisionShadow { get; set; }

    public CollisionShadowStats CollisionShadowStats =>
        CollisionShadow?.Stats ?? default;

    internal CollisionTraversalMode CollisionTraversalMode { get; set; } =
        CollisionTraversalMode.Graph;

    private ConcurrentDictionary<uint, BuildingPhysics> _buildings =>
        _collisionWorld.Current.Buildings;

    public UcgCellGraph CellGraph { get; }

    internal CollisionWorldStateSlot CollisionWorld => _collisionWorld;

    internal PhysicsDataCache CreateEmptyCollisionStaging(
        CollisionWorldStateSlot collisionWorld)
    {
        return new PhysicsDataCache(_requirePreparedCollision, collisionWorld)
        {
            CollisionTraversalMode = CollisionTraversalMode,
            _readFallback = this,
        };
    }

    internal LandblockReplacementBuilder CreateLandblockReplacementBuilder(
        PhysicsDataCache staging,
        uint landblockId,
        uint[] gfxObjectIds,
        uint[] setupIds) => new(
            this,
            staging,
            landblockId,
            gfxObjectIds,
            setupIds);

    public void CacheGfxObj(
        uint gfxObjId,
        GfxObj gfxObj,
        FlatGfxObjCollisionAsset? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(gfxObj);

        if (_requirePreparedCollision
            && prepared is null
            && _flatGfxObj.TryGetValue(gfxObjId, out var retainedPrepared))
        {
            prepared = retainedPrepared;
        }

        if (_requirePreparedCollision &&
            gfxObj.Flags.HasFlag(GfxObjFlags.HasPhysics) &&
            gfxObj.PhysicsBSP?.Root is not null &&
            prepared?.PhysicsBsp is null &&
            !_flatGfxObj.ContainsKey(gfxObjId))
        {
            throw MissingPreparedCollision("GfxObj", gfxObjId);
        }

        if (_requirePreparedCollision)
        {
            if (prepared is not null)
                CachePreparedGfxObj(gfxObjId, prepared);
            return;
        }

        if (prepared is not null)
            _flatGfxObj.TryAdd(gfxObjId, prepared);

        if (!_visualBounds.ContainsKey(gfxObjId) && gfxObj.VertexArray != null)
        {
            _visualBounds[gfxObjId] = ComputeVisualBounds(gfxObj.VertexArray);
        }
        GfxObjVisualBounds? parsedBounds =
            _visualBounds.TryGetValue(gfxObjId, out var cachedBounds)
                ? cachedBounds
                : null;

        if (_gfxObj.TryGetValue(gfxObjId, out GfxObjPhysics? existing))
        {
            if (prepared is not null)
                existing.FlatPhysicsBsp ??= prepared.PhysicsBsp;
            return;
        }
        if (!gfxObj.Flags.HasFlag(GfxObjFlags.HasPhysics)) return;
        if (gfxObj.PhysicsBSP?.Root is null) return;
        if (gfxObj.VertexArray is null) return;

        var physics = new GfxObjPhysics
        {
            SourceId = gfxObjId,
            BSP = gfxObj.PhysicsBSP,
            PhysicsPolygons = gfxObj.PhysicsPolygons,
            BoundingSphere = gfxObj.PhysicsBSP.Root.BoundingSphere,
            Vertices = gfxObj.VertexArray,
            Resolved = ResolvePolygons(gfxObj.PhysicsPolygons, gfxObj.VertexArray),
            FlatPhysicsBsp = prepared?.PhysicsBsp,
            VisualBounds = prepared?.VisualBounds ?? (parsedBounds is { } pb
                ? new FlatGfxObjVisualBounds(
                    pb.Min, pb.Max, pb.Center, pb.Radius, pb.HalfExtents)
                : null),
        };
        _gfxObj[gfxObjId] = physics;

        if (PhysicsDiagnostics.ProbeDumpGfxObjsEnabled
            && PhysicsDiagnostics.ProbeDumpGfxObjIds.Contains(gfxObjId))
        {
            try
            {
                var dump = GfxObjDumpSerializer.Capture(gfxObjId, physics);
                var path = System.IO.Path.Combine(
                    PhysicsDiagnostics.ProbeDumpGfxObjsPath,
                    System.FormattableString.Invariant($"0x{gfxObjId:X8}.gfxobj.json"));
                GfxObjDumpSerializer.Write(dump, path);
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[gfxobj-dump] wrote 0x{gfxObjId:X8} polys={dump.ResolvedPolygons.Count} → {path}"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[gfxobj-dump] FAILED to dump 0x{gfxObjId:X8}: {ex.GetType().Name}: {ex.Message}"));
            }
        }
    }

    /// <summary>
    /// Publishes one package-prepared GfxObj without retaining its parsed DAT
    /// BSP, polygon dictionary, or vertex array.
    /// </summary>
    public void CacheGfxObj(
        uint gfxObjId,
        FlatGfxObjCollisionAsset prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        CachePreparedGfxObj(gfxObjId, prepared);
    }

    private void CachePreparedGfxObj(
        uint gfxObjId,
        FlatGfxObjCollisionAsset prepared)
    {
        _flatGfxObj.TryAdd(gfxObjId, prepared);
        if (prepared.VisualBounds is { } bounds)
        {
            _visualBounds.TryAdd(gfxObjId, new GfxObjVisualBounds
            {
                Min = bounds.Min,
                Max = bounds.Max,
                Center = bounds.Center,
                Radius = bounds.Radius,
                HalfExtents = bounds.HalfExtents,
            });
        }

        if (prepared.PhysicsBsp.RootIndex < 0)
            return;

        FlatCollisionSphere root =
            prepared.PhysicsBsp.Nodes[prepared.PhysicsBsp.RootIndex]
                .BoundingSphere;
        _gfxObj.TryAdd(gfxObjId, new GfxObjPhysics
        {
            SourceId = gfxObjId,
            BoundingSphere = new Sphere
            {
                Origin = root.Origin,
                Radius = root.Radius,
            },
            FlatPhysicsBsp = prepared.PhysicsBsp,
            VisualBounds = prepared.VisualBounds,
        });
    }

    public GfxObjVisualBounds? GetVisualBounds(uint gfxObjId) =>
        _visualBounds.TryGetValue(gfxObjId, out var vb)
            ? vb
            : _readFallback?.GetVisualBounds(gfxObjId);

    internal static GfxObjVisualBounds ComputeVisualBounds(VertexArray vertexArray)
    {
        if (vertexArray.Vertices == null || vertexArray.Vertices.Count == 0)
        {
            return new GfxObjVisualBounds
            {
                Min = Vector3.Zero,
                Max = Vector3.Zero,
                Center = Vector3.Zero,
                Radius = 0f,
                HalfExtents = Vector3.Zero,
            };
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var kv in vertexArray.Vertices)
        {
            var p = kv.Value.Origin;
            if (p.X < min.X) min.X = p.X;
            if (p.Y < min.Y) min.Y = p.Y;
            if (p.Z < min.Z) min.Z = p.Z;
            if (p.X > max.X) max.X = p.X;
            if (p.Y > max.Y) max.Y = p.Y;
            if (p.Z > max.Z) max.Z = p.Z;
        }

        var center = (min + max) * 0.5f;
        var halfExt = (max - min) * 0.5f;
        float radius = halfExt.Length();

        return new GfxObjVisualBounds
        {
            Min = min,
            Max = max,
            Center = center,
            Radius = radius,
            HalfExtents = halfExt,
        };
    }

    public void CacheSetup(
        uint setupId,
        Setup setup,
        FlatSetupCollision? prepared = null)
    {
        ArgumentNullException.ThrowIfNull(setup);

        if (_requirePreparedCollision
            && prepared is null
            && _flatSetup.TryGetValue(setupId, out var retainedPrepared))
        {
            prepared = retainedPrepared;
        }

        if (_requirePreparedCollision &&
            prepared is null &&
            !_flatSetup.ContainsKey(setupId))
            throw MissingPreparedCollision("Setup", setupId);

        if (_requirePreparedCollision)
        {
            CachePreparedSetup(
                setupId,
                prepared
                ?? throw MissingPreparedCollision("Setup", setupId));
            return;
        }

        if (prepared is not null)
            _flatSetup.TryAdd(setupId, prepared);

        if (_setup.TryGetValue(setupId, out SetupPhysics? existing))
        {
            if (prepared is not null)
                existing.FlatCollision ??= prepared;
            return;
        }
        _setup[setupId] = new SetupPhysics
        {
            SourceId = setupId,
            CylSpheres = setup.CylSpheres ?? new(),
            Spheres = setup.Spheres ?? new(),
            Height = setup.Height,
            Radius = setup.Radius,
            StepUpHeight = setup.StepUpHeight,
            StepDownHeight = setup.StepDownHeight,
            FlatCollision = prepared,
        };
    }

    public void CacheSetup(uint setupId, FlatSetupCollision prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        CachePreparedSetup(setupId, prepared);
    }

    private void CachePreparedSetup(uint setupId, FlatSetupCollision prepared)
    {
        _flatSetup.TryAdd(setupId, prepared);
        _setup.TryAdd(setupId, new SetupPhysics
        {
            SourceId = setupId,
            Height = prepared.Height,
            Radius = prepared.Radius,
            StepUpHeight = prepared.StepUpHeight,
            StepDownHeight = prepared.StepDownHeight,
            FlatCollision = prepared,
        });
    }

    public void CacheCellStruct(
        uint envCellId,
        DatReaderWriter.DBObjs.EnvCell envCell,
        CellStruct cellStruct,
        Matrix4x4 worldTransform,
        FlatCellStructureCollisionAsset? preparedStructure = null,
        FlatEnvCellTopology? preparedTopology = null)
    {
        ArgumentNullException.ThrowIfNull(envCell);
        ArgumentNullException.ThrowIfNull(cellStruct);

        if (_requirePreparedCollision)
        {
            if (preparedStructure is null)
                _flatCellStruct.TryGetValue(envCellId, out preparedStructure);
            if (preparedTopology is null)
                _flatEnvCell.TryGetValue(envCellId, out preparedTopology);
        }

        if (_requirePreparedCollision &&
            preparedStructure is null &&
            !_flatCellStruct.ContainsKey(envCellId))
            throw MissingPreparedCollision("CellStruct", envCellId);
        if (_requirePreparedCollision &&
            preparedTopology is null &&
            !_flatEnvCell.ContainsKey(envCellId))
            throw MissingPreparedCollision("EnvCell topology", envCellId);

        if (_requirePreparedCollision)
        {
            CachePreparedCellStruct(
                envCellId,
                envCell,
                worldTransform,
                preparedStructure
                ?? throw MissingPreparedCollision("CellStruct", envCellId),
                preparedTopology
                ?? throw MissingPreparedCollision("EnvCell topology", envCellId));
            return;
        }

        if (cellStruct.CellBSP?.Root is null)
            return;

        if (preparedStructure?.ContainmentBsp.RootIndex < 0)
        {
            preparedStructure = null;
            preparedTopology = null;
        }
        if (preparedStructure is not null)
            _collisionWorld.Current.TryAddFlatCellStruct(envCellId, preparedStructure);
        if (preparedTopology is not null)
            _collisionWorld.Current.TryAddFlatEnvCell(envCellId, preparedTopology);

        if (!CellGraph.Contains(envCellId))
        {
            CellGraph.Add(UcgEnvCell.FromDat(
                envCellId,
                envCell,
                cellStruct,
                worldTransform,
                preparedStructure?.ContainmentBsp));
        }

        if (_cellStruct.ContainsKey(envCellId)) return;

        Matrix4x4.Invert(worldTransform, out var inverseTransform);

        var resolved = cellStruct.PhysicsPolygons is null
            ? new Dictionary<ushort, ResolvedPolygon>()
            : ResolvePolygons(cellStruct.PhysicsPolygons, cellStruct.VertexArray);

        // Visible polygons — portals reference these (NOT PhysicsPolygons).
        var portalPolygons = ResolvePolygons(cellStruct.Polygons, cellStruct.VertexArray);

        var portals = new System.Collections.Generic.List<PortalInfo>(envCell.CellPortals.Count);
        foreach (var p in envCell.CellPortals)
        {
            portals.Add(new PortalInfo(
                otherCellId: p.OtherCellId,
                polygonId:   p.PolygonId,
                flags:       (ushort)p.Flags));
        }

        var visibleCellIds = new System.Collections.Generic.HashSet<uint>();
        if (envCell.VisibleCells is not null)
        {
            uint lbPrefix = envCellId & 0xFFFF0000u;
            foreach (var lowId in envCell.VisibleCells)
                visibleCellIds.Add(lbPrefix | lowId);
        }

        var cellPhysics = new CellPhysics
        {
            SourceId = envCellId,
            BSP = cellStruct.PhysicsBSP,
            PhysicsPolygons = cellStruct.PhysicsPolygons,
            Vertices = cellStruct.VertexArray,
            WorldTransform = worldTransform,
            InverseWorldTransform = inverseTransform,
            Resolved = resolved,
            FlatPhysicsBsp = preparedStructure?.PhysicsBsp,
            FlatContainmentBsp = preparedStructure?.ContainmentBsp,
            FlatPortalPolygons = preparedStructure?.PortalPolygons,
            FlatTopology = preparedTopology,
            CellBSP = cellStruct.CellBSP,
            Portals = portals,
            PortalPolygons = portalPolygons,
            VisibleCellIds = visibleCellIds,
            SeenOutside = envCell.Flags.HasFlag(DatReaderWriter.Enums.EnvCellFlags.SeenOutside),
            RestrictionObj = envCell.RestrictionObj,
        };
        _collisionWorld.Current.SetCellStruct(envCellId, cellPhysics);

        if (PhysicsDiagnostics.ProbeDumpCellsEnabled
            && PhysicsDiagnostics.ProbeDumpCellIds.Contains(envCellId))
        {
            try
            {
                var dump = CellDumpSerializer.Capture(envCellId, cellPhysics);
                var path = System.IO.Path.Combine(
                    PhysicsDiagnostics.ProbeDumpCellsPath,
                    System.FormattableString.Invariant($"0x{envCellId:X8}.json"));
                CellDumpSerializer.Write(dump, path);
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[cell-dump] wrote 0x{envCellId:X8} polys={dump.ResolvedPolygons.Count} portals={dump.Portals.Count} → {path}"));
            }
            catch (Exception ex)
            {
                Console.WriteLine(System.FormattableString.Invariant(
                    $"[cell-dump] FAILED to dump 0x{envCellId:X8}: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        if (PhysicsDiagnostics.ProbeCellCacheEnabled)
        {
            var root = cellStruct.PhysicsBSP?.Root;
            int bspRootPolyCount   = root?.Polygons?.Count ?? 0;
            bool bspRootHasChildren = root?.PosNode is not null || root?.NegNode is not null;

            int bspTotalLeafPolys = 0;
            int bspUnmatchedIds   = 0;
            if (root is not null)
            {
                var stack = new System.Collections.Generic.Stack<DatReaderWriter.Types.PhysicsBSPNode>();
                stack.Push(root);
                while (stack.Count > 0)
                {
                    var n = stack.Pop();
                    if (n.Polygons is not null)
                    {
                        foreach (var pid in n.Polygons)
                        {
                            bspTotalLeafPolys++;
                            if (!resolved.ContainsKey(pid)) bspUnmatchedIds++;
                        }
                    }
                    if (n.PosNode is not null) stack.Push(n.PosNode);
                    if (n.NegNode is not null) stack.Push(n.NegNode);
                }
            }

            var bs = root?.BoundingSphere;
            string bsStr = bs is null
                ? "bsphere=n/a"
                : System.FormattableString.Invariant(
                    $"bsphere=({bs.Origin.X:F2},{bs.Origin.Y:F2},{bs.Origin.Z:F2}) r={bs.Radius:F2}");

            var worldOrigin = Vector3.Transform(Vector3.Zero, worldTransform);

            string portalTargets;
            if (portals.Count == 0)
            {
                portalTargets = "portalTargets=[]";
            }
            else
            {
                var sb = new System.Text.StringBuilder("portalTargets=[");
                for (int i = 0; i < portals.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(System.FormattableString.Invariant(
                        $"(cell=0x{portals[i].OtherCellId:X4},poly=0x{portals[i].PolygonId:X4},flags=0x{portals[i].Flags:X4})"));
                }
                sb.Append(']');
                portalTargets = sb.ToString();
            }

            Console.WriteLine(System.FormattableString.Invariant(
                $"[cell-cache] envCellId=0x{envCellId:X8} physicsPolyCount={cellStruct.PhysicsPolygons?.Count ?? 0} resolvedCount={resolved.Count} bspTotalLeafPolys={bspTotalLeafPolys} bspUnmatchedIds={bspUnmatchedIds} {bsStr} portalCount={portals.Count} visibleCells={visibleCellIds.Count} cellBspRoot={(cellStruct.CellBSP?.Root is null ? "null" : "ok")} worldOrigin=({worldOrigin.X:F2},{worldOrigin.Y:F2},{worldOrigin.Z:F2}) {portalTargets}"));
        }

    }

    public void CacheCellStruct(
        uint envCellId,
        DatReaderWriter.DBObjs.EnvCell envCell,
        Matrix4x4 worldTransform,
        FlatCellStructureCollisionAsset preparedStructure,
        FlatEnvCellTopology preparedTopology)
    {
        ArgumentNullException.ThrowIfNull(envCell);
        ArgumentNullException.ThrowIfNull(preparedStructure);
        ArgumentNullException.ThrowIfNull(preparedTopology);
        CachePreparedCellStruct(
            envCellId,
            envCell,
            worldTransform,
            preparedStructure,
            preparedTopology);
    }

    private void CachePreparedCellStruct(
        uint envCellId,
        DatReaderWriter.DBObjs.EnvCell envCell,
        Matrix4x4 worldTransform,
        FlatCellStructureCollisionAsset preparedStructure,
        FlatEnvCellTopology preparedTopology)
    {
        if (preparedStructure.ContainmentBsp.RootIndex < 0)
            return;

        _collisionWorld.Current.TryAddFlatCellStruct(envCellId, preparedStructure);
        _collisionWorld.Current.TryAddFlatEnvCell(envCellId, preparedTopology);

        if (!CellGraph.Contains(envCellId))
        {
            CellGraph.Add(UcgEnvCell.FromPrepared(
                envCellId,
                worldTransform,
                preparedStructure,
                preparedTopology));
        }

        if (_cellStruct.ContainsKey(envCellId))
            return;

        Matrix4x4.Invert(worldTransform, out Matrix4x4 inverseTransform);
        var portals = new List<PortalInfo>(preparedTopology.Portals.Length);
        for (int i = 0; i < preparedTopology.Portals.Length; i++)
        {
            FlatEnvCellPortal portal = preparedTopology.Portals[i];
            portals.Add(new PortalInfo(
                portal.OtherCellId,
                portal.PolygonId,
                portal.Flags));
        }

        _collisionWorld.Current.TryAddCellStruct(envCellId, new CellPhysics
        {
            SourceId = envCellId,
            WorldTransform = worldTransform,
            InverseWorldTransform = inverseTransform,
            Resolved = new Dictionary<ushort, ResolvedPolygon>(),
            FlatPhysicsBsp = preparedStructure.PhysicsBsp,
            FlatContainmentBsp = preparedStructure.ContainmentBsp,
            FlatPortalPolygons = preparedStructure.PortalPolygons,
            FlatTopology = preparedTopology,
            Portals = portals,
            VisibleCellIds = new HashSet<uint>(
                preparedTopology.VisibleCellIds),
            SeenOutside = preparedTopology.SeenOutside,
            RestrictionObj = envCell.RestrictionObj,
        });
    }

    internal static Dictionary<ushort, ResolvedPolygon> ResolvePolygons(
        Dictionary<ushort, DatReaderWriter.Types.Polygon> polys,
        VertexArray vertexArray)
    {
        var resolved = new Dictionary<ushort, ResolvedPolygon>(polys.Count);
        foreach (var (id, poly) in polys)
        {
            int numVerts = poly.VertexIds.Count;
            if (numVerts < 3) continue;

            var verts = new Vector3[numVerts];
            bool valid = true;
            for (int i = 0; i < numVerts; i++)
            {
                ushort vid = (ushort)poly.VertexIds[i];
                if (!vertexArray.Vertices.TryGetValue(vid, out var sv))
                { valid = false; break; }
                verts[i] = sv.Origin;
            }
            if (!valid) continue;

            var normal = Vector3.Zero;
            for (int i = 1; i < numVerts - 1; i++)
            {
                var v1 = verts[i] - verts[0];
                var v2 = verts[i + 1] - verts[0];
                normal += Vector3.Cross(v1, v2);
            }
            float len = normal.Length();
            if (len < 1e-8f) continue;
            normal /= len;

            // D = -(average dot(normal, vertex))
            float dotSum = 0f;
            for (int i = 0; i < numVerts; i++)
                dotSum += Vector3.Dot(normal, verts[i]);
            float d = -(dotSum / numVerts);

            resolved[id] = new ResolvedPolygon
            {
                Vertices = verts,
                Plane = new Plane(normal, d),
                NumPoints = numVerts,
                SidesType = poly.SidesType,
                Id = id,
            };
        }
        return resolved;
    }

    private static InvalidOperationException MissingPreparedCollision(
        string kind,
        uint sourceId) =>
        new(
            $"Production {kind} 0x{sourceId:X8} has no prepared collision asset. " +
            "Gameplay must not extract or fall back to a parsed DAT graph.");

    public GfxObjPhysics? GetGfxObj(uint id) =>
        _gfxObj.TryGetValue(id, out var p)
            ? p
            : _readFallback?.GetGfxObj(id);

    public SetupPhysics? GetSetup(uint id) =>
        _setup.TryGetValue(id, out var p)
            ? p
            : _readFallback?.GetSetup(id);
    public CellPhysics? GetCellStruct(uint id) => _cellStruct.TryGetValue(id, out var p) ? p : null;
    public FlatGfxObjCollisionAsset? GetFlatGfxObj(uint id) =>
        _flatGfxObj.TryGetValue(id, out var value)
            ? value
            : _readFallback?.GetFlatGfxObj(id);
    public FlatSetupCollision? GetFlatSetup(uint id) =>
        _flatSetup.TryGetValue(id, out var value)
            ? value
            : _readFallback?.GetFlatSetup(id);
    public FlatCellStructureCollisionAsset? GetFlatCellStruct(uint id) =>
        _flatCellStruct.TryGetValue(id, out var value) ? value : null;
    public FlatEnvCellTopology? GetFlatEnvCell(uint id) =>
        _flatEnvCell.TryGetValue(id, out var value) ? value : null;
    public int GfxObjCount => _gfxObj.Count;
    public int SetupCount => _setup.Count;
    public int CellStructCount => _cellStruct.Count;
    public int FlatGfxObjCount => _flatGfxObj.Count;
    public int FlatSetupCount => _flatSetup.Count;
    public int FlatCellStructCount => _flatCellStruct.Count;
    public int FlatEnvCellCount => _flatEnvCell.Count;
    public int GraphGfxObjCount => CountRetainedGfxObjGraphs();
    public int GraphSetupCount => CountRetainedSetupGraphs();
    public int GraphCellStructCount => CountRetainedCellGraphs();

    private int CountRetainedGfxObjGraphs()
    {
        int count = 0;
        foreach (GfxObjPhysics value in _gfxObj.Values)
        {
            if (value.BSP is not null
                || value.PhysicsPolygons is not null
                || value.Vertices is not null
                || value.Resolved.Count != 0)
            {
                count++;
            }
        }
        return count;
    }

    private int CountRetainedSetupGraphs()
    {
        int count = 0;
        foreach (SetupPhysics value in _setup.Values)
        {
            if (value.CylSpheres.Count != 0 || value.Spheres.Count != 0)
                count++;
        }
        return count;
    }

    private int CountRetainedCellGraphs()
    {
        int count = 0;
        foreach (CellPhysics value in _cellStruct.Values)
        {
            if (value.BSP is not null
                || value.CellBSP is not null
                || value.PhysicsPolygons is not null
                || value.Vertices is not null
                || value.Resolved.Count != 0
                || value.PortalPolygons is not null)
            {
                count++;
            }
        }
        return count;
    }

    public IReadOnlyCollection<uint> CellStructIds => (IReadOnlyCollection<uint>)_cellStruct.Keys;

    public void RegisterGfxObjForTest(uint gfxObjId, GfxObjPhysics physics)
        => _gfxObj[gfxObjId] = physics;

    public void RegisterCellStructForTest(uint envCellId, CellPhysics physics)
        => _collisionWorld.Current.SetCellStruct(envCellId, physics);

    public void CacheBuilding(uint landcellId, IReadOnlyList<BldPortalInfo> portals, Matrix4x4 worldTransform,
                              uint modelId = 0u)
    {
        if (_buildings.ContainsKey(landcellId)) return;
        Matrix4x4.Invert(worldTransform, out var inverse);
        _collisionWorld.Current.SetBuilding(landcellId, new BuildingPhysics
        {
            WorldTransform = worldTransform,
            InverseWorldTransform = inverse,
            Portals = portals,
            ModelId = modelId,
        });
    }

    public void RemoveBuildingsForLandblock(uint landblockId)
    {
        CollisionWorldState world = _collisionWorld.Current;
        RemovePrefixKeys(
            world.BuildingKeys,
            landblockId & 0xFFFF0000u,
            world.RemoveBuilding);
    }

    private static void RemovePrefixKeys(
        PrefixKeyIndex ledger,
        uint prefix,
        Func<uint, bool> remove)
    {
        List<uint>? slots = ledger.SlotsForPrefix(prefix);
        if (slots is null)
            return;
        int limit = slots.Count;
        for (int index = 0; index < limit; index++)
        {
            uint key = slots[index];
            if (key != 0u)
                remove(key);
        }
    }

    public void RemoveCellsForLandblock(uint landblockId)
    {
        uint prefix = landblockId & 0xFFFF0000u;
        CollisionWorldState world = _collisionWorld.Current;
        RemovePrefixKeys(world.CellStructKeys, prefix, world.RemoveCellStruct);
        RemovePrefixKeys(
            world.FlatCellStructKeys,
            prefix,
            world.RemoveFlatCellStruct);
        RemovePrefixKeys(world.FlatEnvCellKeys, prefix, world.RemoveFlatEnvCell);
    }

    public BuildingPhysics? GetBuilding(uint landcellId)
        => _buildings.TryGetValue(landcellId, out var b) ? b : null;

    public IReadOnlyCollection<uint> BuildingIds => (IReadOnlyCollection<uint>)_buildings.Keys;

    public void RegisterBuildingForTest(uint landcellId, BuildingPhysics b) =>
        _collisionWorld.Current.SetBuilding(landcellId, b);

    internal sealed class LandblockReplacementBuilder : IDisposable
    {
        private readonly PhysicsDataCache _active;
        private readonly PhysicsDataCache _staging;
        private readonly uint _prefix;
        private readonly uint[] _gfxIds;
        private readonly uint[] _setupIds;
        private readonly List<KeyValuePair<uint, GfxObjPhysics>> _gfx = new();
        private readonly List<KeyValuePair<uint, GfxObjVisualBounds>> _bounds = new();
        private readonly List<KeyValuePair<uint, FlatGfxObjCollisionAsset>> _flatGfx = new();
        private readonly List<KeyValuePair<uint, SetupPhysics>> _setups = new();
        private readonly List<KeyValuePair<uint, FlatSetupCollision>> _flatSetups = new();
        private readonly List<KeyValuePair<uint, CellPhysics>> _cells = new();
        private readonly List<KeyValuePair<uint, FlatCellStructureCollisionAsset>> _flatCells = new();
        private readonly List<KeyValuePair<uint, FlatEnvCellTopology>> _flatEnvCells = new();
        private readonly List<KeyValuePair<uint, BuildingPhysics>> _buildings = new();
        private readonly HashSet<uint> _cellIds = new();
        private readonly HashSet<uint> _flatCellIds = new();
        private readonly HashSet<uint> _flatEnvCellIds = new();
        private readonly HashSet<uint> _buildingIds = new();
        private readonly List<uint> _removeCells = new();
        private readonly List<uint> _removeFlatCells = new();
        private readonly List<uint> _removeFlatEnvCells = new();
        private readonly List<uint> _removeBuildings = new();
        private readonly UcgCellGraph.LandblockReplacementBuilder _cellGraph;
        private List<uint>? _keySlots;
        private int _keySlotLimit;
        private bool _keySlotsCaptured;
        private int _phase;
        private int _cursor;

        internal LandblockReplacementBuilder(
            PhysicsDataCache active,
            PhysicsDataCache staging,
            uint landblockId,
            uint[] gfxIds,
            uint[] setupIds)
        {
            _active = active;
            _staging = staging;
            _prefix = landblockId & 0xFFFF0000u;
            _gfxIds = gfxIds;
            _setupIds = setupIds;
            _cellGraph = active.CellGraph.CreateLandblockReplacementBuilder(
                staging.CellGraph,
                _prefix);
        }

        internal int WorkUnits { get; private set; }
        internal PreparedPhysicsDataCacheLandblock? Prepared { get; private set; }

        internal bool Advance()
        {
            switch (_phase)
            {
                case 0:
                    if (_cursor < _gfxIds.Length)
                    {
                        uint id = _gfxIds[_cursor++];
                        Preinstall(_staging._gfxObj, _active._gfxObj, id);
                        Preinstall(_staging._visualBounds, _active._visualBounds, id);
                        Preinstall(_staging._flatGfxObj, _active._flatGfxObj, id);
                        Capture(_staging._gfxObj, id, _gfx);
                        Capture(_staging._visualBounds, id, _bounds);
                        Capture(_staging._flatGfxObj, id, _flatGfx);
                        WorkUnits++;
                        return false;
                    }
                    _cursor = 0;
                    _phase++;
                    return false;
                case 1:
                    if (_cursor < _setupIds.Length)
                    {
                        uint id = _setupIds[_cursor++];
                        Preinstall(_staging._setup, _active._setup, id);
                        Preinstall(_staging._flatSetup, _active._flatSetup, id);
                        Capture(_staging._setup, id, _setups);
                        Capture(_staging._flatSetup, id, _flatSetups);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                case 2:
                {
                    // O1: enumerate the staging root's installed target-prefix
                    // keys instead of scanning the whole staging map, one key
                    // per advance.
                    if (TryTakeNextPrefixKey(
                            StagingWorld.CellStructKeys,
                            out uint id))
                    {
                        CaptureInstall(_staging._cellStruct, id, _cells, _cellIds);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 3:
                {
                    if (TryTakeNextPrefixKey(
                            ActiveWorld.CellStructKeys,
                            out uint id))
                    {
                        CaptureRemoval(_active._cellStruct, id, _cellIds, _removeCells);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 4:
                {
                    if (TryTakeNextPrefixKey(
                            StagingWorld.FlatCellStructKeys,
                            out uint id))
                    {
                        CaptureInstall(
                            _staging._flatCellStruct,
                            id,
                            _flatCells,
                            _flatCellIds);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 5:
                {
                    if (TryTakeNextPrefixKey(
                            ActiveWorld.FlatCellStructKeys,
                            out uint id))
                    {
                        CaptureRemoval(
                            _active._flatCellStruct,
                            id,
                            _flatCellIds,
                            _removeFlatCells);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 6:
                {
                    if (TryTakeNextPrefixKey(
                            StagingWorld.FlatEnvCellKeys,
                            out uint id))
                    {
                        CaptureInstall(
                            _staging._flatEnvCell,
                            id,
                            _flatEnvCells,
                            _flatEnvCellIds);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 7:
                {
                    if (TryTakeNextPrefixKey(
                            ActiveWorld.FlatEnvCellKeys,
                            out uint id))
                    {
                        CaptureRemoval(
                            _active._flatEnvCell,
                            id,
                            _flatEnvCellIds,
                            _removeFlatEnvCells);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 8:
                {
                    if (TryTakeNextPrefixKey(
                            StagingWorld.BuildingKeys,
                            out uint id))
                    {
                        CaptureInstall(
                            _staging._buildings,
                            id,
                            _buildings,
                            _buildingIds);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 9:
                {
                    if (TryTakeNextPrefixKey(
                            ActiveWorld.BuildingKeys,
                            out uint id))
                    {
                        CaptureRemoval(
                            _active._buildings,
                            id,
                            _buildingIds,
                            _removeBuildings);
                        WorkUnits++;
                        return false;
                    }
                    _phase++;
                    return false;
                }
                case 10:
                    WorkUnits++;
                    if (!_cellGraph.Advance())
                        return false;
                    Prepared = new PreparedPhysicsDataCacheLandblock(
                        _prefix,
                        _gfx,
                        _bounds,
                        _flatGfx,
                        _setups,
                        _flatSetups,
                        _removeCells,
                        _cells,
                        _removeFlatCells,
                        _flatCells,
                        _removeFlatEnvCells,
                        _flatEnvCells,
                        _removeBuildings,
                        _buildings,
                        _cellGraph.Prepared!);
                    _phase++;
                    return true;
                default:
                    return true;
            }
        }

        private static void Capture<T>(
            ConcurrentDictionary<uint, T> source,
            uint id,
            List<KeyValuePair<uint, T>> destination)
        {
            if (source.TryGetValue(id, out T? value))
                destination.Add(new KeyValuePair<uint, T>(id, value));
        }

        private static void Preinstall<T>(
            ConcurrentDictionary<uint, T> source,
            ConcurrentDictionary<uint, T> destination,
            uint id)
        {
            if (source.TryGetValue(id, out T? value))
                destination.TryAdd(id, value);
        }

        private CollisionWorldState StagingWorld =>
            _staging._collisionWorld.Current;

        private CollisionWorldState ActiveWorld =>
            _active._collisionWorld.Current;

        private bool TryTakeNextPrefixKey(PrefixKeyIndex ledger, out uint key)
        {
            if (!_keySlotsCaptured)
            {
                _keySlots = ledger.SlotsForPrefix(_prefix);
                _keySlotLimit = _keySlots?.Count ?? 0;
                _keySlotsCaptured = true;
                _cursor = 0;
            }
            while (_cursor < _keySlotLimit)
            {
                uint candidate = _keySlots![_cursor++];
                if (candidate != 0u)
                {
                    key = candidate;
                    return true;
                }
            }
            key = 0u;
            _keySlots = null;
            _keySlotsCaptured = false;
            return false;
        }

        private static void CaptureInstall<T>(
            ConcurrentDictionary<uint, T> source,
            uint id,
            List<KeyValuePair<uint, T>> destination,
            HashSet<uint> ids)
        {
            if (source.TryGetValue(id, out T? value))
            {
                destination.Add(new KeyValuePair<uint, T>(id, value));
                ids.Add(id);
            }
        }

        private static void CaptureRemoval<T>(
            ConcurrentDictionary<uint, T> source,
            uint id,
            HashSet<uint> retained,
            List<uint> destination)
        {
            if (!retained.Contains(id) && source.ContainsKey(id))
                destination.Add(id);
        }

        public void Dispose()
        {
            _keySlots = null;
            _keySlotsCaptured = false;
            _cellGraph.Dispose();
        }
    }
}

internal sealed record PreparedPhysicsDataCacheLandblock(
    uint LandblockPrefix,
    IReadOnlyList<KeyValuePair<uint, GfxObjPhysics>> GfxObjects,
    IReadOnlyList<KeyValuePair<uint, GfxObjVisualBounds>> VisualBounds,
    IReadOnlyList<KeyValuePair<uint, FlatGfxObjCollisionAsset>> FlatGfxObjects,
    IReadOnlyList<KeyValuePair<uint, SetupPhysics>> Setups,
    IReadOnlyList<KeyValuePair<uint, FlatSetupCollision>> FlatSetups,
    IReadOnlyList<uint> CellIdsToRemove,
    IReadOnlyList<KeyValuePair<uint, CellPhysics>> Cells,
    IReadOnlyList<uint> FlatCellIdsToRemove,
    IReadOnlyList<KeyValuePair<uint, FlatCellStructureCollisionAsset>> FlatCells,
    IReadOnlyList<uint> FlatEnvCellIdsToRemove,
    IReadOnlyList<KeyValuePair<uint, FlatEnvCellTopology>> FlatEnvCells,
    IReadOnlyList<uint> BuildingIdsToRemove,
    IReadOnlyList<KeyValuePair<uint, BuildingPhysics>> Buildings,
    PreparedCellGraphLandblock CellGraph);

public sealed class GfxObjVisualBounds
{
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public required Vector3 Center { get; init; }
    /// <summary>Local-space radius (diagonal half-length) — loose bound.</summary>
    public required float Radius { get; init; }
    /// <summary>Local-space half-extents ((Max - Min) * 0.5).</summary>
    public required Vector3 HalfExtents { get; init; }
}

public sealed class ResolvedPolygon
{
    public required Vector3[] Vertices { get; init; }
    public required Plane Plane { get; init; }
    public required int NumPoints { get; init; }
    public required CullMode SidesType { get; init; }
    public ushort Id { get; init; }
}

public sealed class GfxObjPhysics
{
    public uint SourceId { get; init; }
    public PhysicsBSPTree? BSP { get; init; }
    public Dictionary<ushort, Polygon>? PhysicsPolygons { get; init; }
    public Sphere? BoundingSphere { get; init; }
    public VertexArray? Vertices { get; init; }

    public Dictionary<ushort, ResolvedPolygon> Resolved { get; init; } = new();

    public FlatPhysicsBsp? FlatPhysicsBsp { get; internal set; }

    public FlatGfxObjVisualBounds? VisualBounds { get; init; }
}

public sealed class SetupPhysics
{
    public uint SourceId { get; init; }
    public List<CylSphere> CylSpheres { get; init; } = new();
    public List<Sphere> Spheres { get; init; } = new();
    public float Height { get; init; }
    public float Radius { get; init; }
    public float StepUpHeight { get; init; }
    public float StepDownHeight { get; init; }
    public FlatSetupCollision? FlatCollision { get; internal set; }
}

public sealed class CellPhysics
{
    public uint SourceId { get; init; }
    public PhysicsBSPTree? BSP { get; init; }
    public Dictionary<ushort, Polygon>? PhysicsPolygons { get; init; }
    public VertexArray? Vertices { get; init; }
    public Matrix4x4 WorldTransform { get; init; }
    public Matrix4x4 InverseWorldTransform { get; init; }

    public required Dictionary<ushort, ResolvedPolygon> Resolved { get; init; }

    public FlatPhysicsBsp? FlatPhysicsBsp { get; init; }


    public DatReaderWriter.Types.CellBSPTree? CellBSP { get; init; }

    public FlatCellContainmentBsp? FlatContainmentBsp { get; init; }

    public FlatPolygonTable? FlatPortalPolygons { get; init; }

    /// <summary>
    /// Prepared per-EnvCell portal/visibility state. Placement remains on this
    /// runtime record's world transforms.
    /// </summary>
    public FlatEnvCellTopology? FlatTopology { get; init; }

    public IReadOnlyList<PortalInfo> Portals { get; init; } = System.Array.Empty<PortalInfo>();

    public Dictionary<ushort, ResolvedPolygon>? PortalPolygons { get; init; }

    public IReadOnlySet<uint> VisibleCellIds { get; init; } = new System.Collections.Generic.HashSet<uint>();

    public bool SeenOutside { get; init; }

    public uint RestrictionObj { get; init; }
}
