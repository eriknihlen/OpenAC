using System.Collections;
using System.Collections.Immutable;
using System.Numerics;
using System.Reflection;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using Xunit;

namespace AcDream.Core.Tests.Physics;

public sealed class CellTransitBuildingPartsTests
{
    private const uint Prefix = 0xA9B40000u;
    private const uint Outside = Prefix | 1u;
    private const uint A = Prefix | 0x100u;
    private const uint B = Prefix | 0x101u;
    private const uint C = Prefix | 0x102u;
    private const float Epsilon = 0.000199999995f;
    private static readonly Vector3 Origin = new(12, 12, 0);
    private static readonly Plane Permissive = new(Vector3.UnitZ, 100);
    private static readonly PortalSpec DefaultPortal = new(0xFFFF, new Plane(Vector3.UnitX, 0), 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CathedralLikeSphereHit_BoxBelowDestinationPlane_RejectsOnlyPartRoute(bool prepared)
    {
        PhysicsDataCache cache = Cache(prepared);
        CellPhysics cell = Cell(A, prepared, new Plane(Vector3.UnitZ, -21.8948f), DefaultPortal);
        cache.RegisterCellStructForTest(A, cell);
        BuildingPhysics building = Building(new BldPortalInfo(A, 0, 0));
        cache.RegisterBuildingForTest(Outside, building);
        ShadowPartBox[] boxes = [Box(new(-1, -1, 16.86699f), new(1, 1, 21.894302f))];
        Sphere[] spheres = [SphereAt(new(0, 0, 19.380646f), 3.93215f)];

        var sphereCells = new CellArray();
        CellTransit.CheckBuildingTransit(cache, building, spheres, 1, sphereCells, out bool hits);
        Assert.True(hits);
        Assert.Equal(new[] { A }, sphereCells.OrderedIds);
        Assert.Equal(new[] { Outside }, Flood(cache, boxes, spheres));
        if (prepared)
        {
            Assert.Null(cell.PortalPolygons);
            Assert.Equal((ushort)42, cell.Portals[0].PolygonId);
            Assert.Equal(1, cell.FlatTopology!.Portals[0].PolygonIndex);
            Assert.Equal((ushort)7, cell.FlatPortalPolygons!.Polygons[0].Id);
        }
    }

    [Theory]
    [InlineData(false, -1, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, -1, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    public void ReciprocalSide_AdmitsOnlyCrossingOrSameSide(bool negativeSide, int boxSide, bool admitted)
    {
        PhysicsDataCache cache = Cache(true);
        ushort flags = negativeSide ? (ushort)0 : (ushort)2;
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive,
            new PortalSpec(0xFFFF, new Plane(Vector3.UnitZ, 0), flags)));
        // Opposite building flags must not replace the destination's reciprocal side.
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, (ushort)(flags ^ 2))));
        float center = boxSide * 0.5f;
        ShadowPartBox[] boxes = [Box(new(-0.1f, -0.1f, center - 0.1f), new(0.1f, 0.1f, center + 0.1f))];
        IReadOnlyList<uint> result = Flood(cache, boxes, [SphereAt(Vector3.Zero, 1)]);
        Assert.Equal(admitted ? new[] { Outside, A } : new[] { Outside }, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void BuildingCheapGate_EqualityAdmits_AdjacentOutsideFloatRejects(bool negativeSide, bool beyond)
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive,
            new PortalSpec(0xFFFF, new Plane(Vector3.UnitZ, 0), negativeSide ? (ushort)0 : (ushort)2)));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, 0)));
        float padded = 0.5f + Epsilon;
        float z = negativeSide ? padded : -padded;
        if (beyond) z = negativeSide ? MathF.BitIncrement(z) : MathF.BitDecrement(z);
        float boxZ = negativeSide ? padded - 0.5f : 0.5f - padded;
        Assert.InRange(MathF.Abs(boxZ), 0f, MathF.BitDecrement(Epsilon));
        ShadowPartBox[] boxes = [Box(new(0, 0, boxZ), new(0, 0, boxZ))];
        Assert.Equal(beyond ? new[] { Outside } : new[] { Outside, A },
            Flood(cache, boxes, [SphereAt(new(0, 0, z), 0.5f)]));
    }

    [Fact]
    public void EarlierPartRejects_LaterPartAdmits_WithoutChangingTheirOrder()
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive,
            new PortalSpec(0xFFFF, new Plane(Vector3.UnitZ, 0), 2)));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, 0)));
        ShadowPartBox[] boxes = [Box(new(-0.1f, -0.1f, -2), new(0.1f, 0.1f, -1)),
            Box(new(-0.1f, -0.1f, 1), new(0.1f, 0.1f, 2))];
        Sphere[] spheres = [SphereAt(Vector3.Zero, 3), SphereAt(Vector3.Zero, 3)];
        Assert.Equal(new[] { Outside }, Flood(cache, boxes[..1], spheres[..1]));
        Assert.Equal(new[] { Outside, A }, Flood(cache, boxes, spheres));
    }

    [Fact]
    public void BuildingDestination_ExpandsBeforeNextAuthoredPortal()
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal,
            new PortalSpec(0x102, new Plane(Vector3.UnitX, 0), 0)));
        cache.RegisterCellStructForTest(B, Cell(B, true, Permissive, DefaultPortal));
        cache.RegisterCellStructForTest(C, Cell(C, true, Permissive));
        cache.RegisterBuildingForTest(Outside, Building(new(A, 0, 0), new(B, 0, 0)));
        Assert.Equal(new[] { Outside, A, C, B }, Flood(cache, [SmallBox()], [SphereAt(Vector3.Zero, 1)]));
    }

    [Fact]
    public void ImmediateDestinationExpansion_ReceivesAllParts_NotJustTheBuildingHit()
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive,
            new PortalSpec(0xFFFF, new Plane(Vector3.UnitZ, 0), 2),
            new PortalSpec(0x102, new Plane(Vector3.UnitX, 0), 0)));
        cache.RegisterCellStructForTest(B, Cell(B, true, Permissive, DefaultPortal));
        cache.RegisterCellStructForTest(C, Cell(C, true, Permissive));
        cache.RegisterBuildingForTest(Outside, Building(new(A, 0, 0), new(B, 0, 0)));
        ShadowPartBox[] boxes = [Box(new(-1, -0.1f, 0.1f), new(-0.5f, 0.1f, 0.2f)),
            Box(new(0.5f, -0.1f, 0.1f), new(1, 0.1f, 0.2f))];
        Assert.Equal(new[] { Outside, A, C, B }, Flood(cache, boxes,
            [SphereAt(Vector3.Zero, 2), SphereAt(Vector3.Zero, 2)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateDestination_StillExpandsImmediately_WithSharedOutsideLatch(bool alreadyOutside)
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal,
            new PortalSpec(0x102, new Plane(Vector3.UnitX, 0), 0)));
        cache.RegisterCellStructForTest(B, Cell(B, true, Permissive, DefaultPortal));
        cache.RegisterCellStructForTest(C, Cell(C, true, Permissive));
        var candidates = new CellArray();
        candidates.Add(A);
        MethodInfo helper = Assert.IsAssignableFrom<MethodInfo>(typeof(CellTransit).GetMethod(
            "CheckBuildingTransitFromParts", BindingFlags.Static | BindingFlags.NonPublic));
        object[] arguments = [cache, Building(new(A, 0, 0), new(B, 0, 0)),
            new[] { SmallBox() }, new[] { SphereAt(Vector3.Zero, 1) }, candidates,
            Outside, Vector3.Zero, alreadyOutside];
        helper.Invoke(null, arguments);
        Assert.True((bool)arguments[7]);
        Assert.Equal(alreadyOutside ? new[] { A, C, B } : new[] { A, C, Outside, B }, candidates.OrderedIds);
    }

    [Fact]
    public void FirstHitBreaksPartLoop_ImmediateAndOuterDestinationVisitsRemainDistinct()
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, 0)));
        var spheres = new ReadTrackingSpheres([SphereAt(Vector3.Zero, 1), SphereAt(Vector3.Zero, 1)]);
        Assert.Equal(new[] { Outside, A }, Flood(cache, [SmallBox(), SmallBox()], spheres));
        // Building hit, immediate destination portal, then growing-array destination portal.
        Assert.Equal(new[] { 0, 0, 0 }, spheres.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(short.MinValue)]
    public void NegativeReciprocal_IsSkipped(short reciprocal)
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, reciprocal, 0)));
        Assert.Equal(new[] { Outside }, Flood(cache, [SmallBox()], [SphereAt(Vector3.Zero, 1)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableOrRootlessDestination_IsSkippedBeforeReciprocalValidation(bool rootless)
    {
        PhysicsDataCache cache = Cache(true);
        if (rootless) cache.RegisterCellStructForTest(A, Cell(A, true, null, DefaultPortal));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 300, 0)));
        Assert.Equal(new[] { Outside }, Flood(cache, [SmallBox()], [SphereAt(Vector3.Zero, 1)]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void MalformedPositiveReciprocal_ThrowsExplicitContentIntegrityFailure(short reciprocal)
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, reciprocal, 0)));
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() =>
            Flood(cache, [SmallBox()], [SphereAt(Vector3.Zero, 1)]));
        Assert.Equal($"Building portal to cell 0xA9B40100 references reciprocal portal {reciprocal}, but the destination has 1 portals.", failure.Message);
    }

    [Fact]
    public void EmptyInputs_PreserveExistingSeedAndPartGuards()
    {
        PhysicsDataCache cache = Cache(true);
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, 0)));
        Assert.Empty(Flood(cache, [], []));
        Assert.Equal(new[] { Outside }, Flood(cache, [SmallBox()], []));
        Assert.Empty(CellTransit.BuildShadowCellSetFromParts(cache, 0, [SmallBox()], [SphereAt(Vector3.Zero, 1)], false));
    }

    [Fact]
    public void UnloadedActiveOutdoorCell_PreservesOutsideIdsWithoutWalkingCachedBuilding()
    {
        PhysicsDataCache cache = Cache(true, terrain: false);
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal));
        cache.RegisterBuildingForTest(Outside, Building(new BldPortalInfo(A, 0, 0)));
        Assert.Equal(new[] { Outside }, Flood(cache, [SmallBox()], [SphereAt(Vector3.Zero, 1)]));
    }

    [Fact]
    public void UnloadedLaterOutdoorCandidate_DoesNotWalkItsCachedBuilding()
    {
        PhysicsDataCache cache = Cache(true);
        const uint adjacentOutside = 0xAAB40001u;
        cache.RegisterCellStructForTest(A, Cell(A, true, Permissive, DefaultPortal));
        cache.RegisterBuildingForTest(adjacentOutside, Building(new BldPortalInfo(A, 0, 0)));
        ShadowPartBox[] boxes = [Box(new(-1, -1, -0.1f), new(1, 1, 0.1f), new Vector3(192, 12, 0))];
        IReadOnlyList<uint> result = CellTransit.BuildShadowCellSetFromParts(
            cache, Prefix | 57u, boxes, [SphereAt(new(180, 0, 0), 2)], false);
        Assert.Equal(new[] { Prefix | 57u, adjacentOutside }, result);
    }

    private static PhysicsDataCache Cache(bool prepared, bool terrain = true)
    {
        PhysicsDataCache cache = prepared ? PhysicsDataCache.CreateProduction() : new PhysicsDataCache();
        if (terrain) cache.CellGraph.RegisterTerrain(Prefix, new TerrainSurface(new byte[81], new float[256]), Vector3.Zero);
        return cache;
    }

    private static IReadOnlyList<uint> Flood(PhysicsDataCache cache, IReadOnlyList<ShadowPartBox> boxes, IReadOnlyList<Sphere> spheres) =>
        CellTransit.BuildShadowCellSetFromParts(cache, Outside, boxes, spheres, isStatic: true);

    private static BuildingPhysics Building(params BldPortalInfo[] portals) => new()
    {
        WorldTransform = Matrix4x4.Identity, InverseWorldTransform = Matrix4x4.Identity, Portals = portals,
    };

    private readonly record struct PortalSpec(ushort Other, Plane Plane, ushort Flags);

    private static CellPhysics Cell(uint id, bool prepared, Plane? containment, params PortalSpec[] specs)
    {
        CellBSPNode? root = containment is { } plane ? new CellBSPNode
        {
            Type = BSPNodeType.BPOL, SplittingPlane = plane, PosNode = null, NegNode = null,
        } : null;
        // Polygon ID 42 is deliberately at flat table index1, not index0 or42.
        var polygons = new Dictionary<ushort, ResolvedPolygon>
        {
            [7] = new() { Id = 7, Plane = new Plane(Vector3.UnitZ, -1000), Vertices = [], NumPoints = 0, SidesType = CullMode.None },
        };
        var portals = new List<PortalInfo>();
        var flatPortals = ImmutableArray.CreateBuilder<FlatEnvCellPortal>();
        for (int i = 0; i < specs.Length; i++)
        {
            ushort polygonId = checked((ushort)(42 + i));
            PortalSpec spec = specs[i];
            polygons.Add(polygonId, new ResolvedPolygon { Id = polygonId, Plane = spec.Plane, Vertices = [], NumPoints = 0, SidesType = CullMode.None });
            portals.Add(new PortalInfo(spec.Other, polygonId, spec.Flags));
            flatPortals.Add(new FlatEnvCellPortal(spec.Other, polygonId, spec.Flags, i + 1));
        }
        return new CellPhysics
        {
            SourceId = id, WorldTransform = Matrix4x4.CreateTranslation(Origin),
            InverseWorldTransform = Matrix4x4.CreateTranslation(-Origin), Resolved = [],
            CellBSP = prepared ? null : new CellBSPTree { Root = root },
            FlatContainmentBsp = FlatCollisionAssetBuilder.FlattenCellContainmentBsp(root),
            Portals = portals, PortalPolygons = prepared ? null : polygons,
            FlatPortalPolygons = new FlatPolygonTable(polygons.Values.OrderBy(p => p.Id)
                .Select(p => new FlatCollisionPolygon(p.Id, p.Plane, p.SidesType, 0, new FlatIndexRange(0, 0))).ToImmutableArray(), []),
            FlatTopology = new FlatEnvCellTopology(flatPortals.ToImmutable(), [], true),
        };
    }

    private static Sphere SphereAt(Vector3 localCenter, float radius) => new() { Origin = Origin + localCenter, Radius = radius };
    private static ShadowPartBox SmallBox() => Box(new(-0.1f), new(0.1f));

    private static ShadowPartBox Box(Vector3 min, Vector3 max, Vector3? worldPosition = null)
    {
        var geometry = ShadowPartGeometry.Create(new FlatCollisionSphere(Vector3.Zero, 1),
            new FlatGfxObjVisualBounds(min, max, (min + max) * 0.5f, ((max - min) * 0.5f).Length(), (max - min) * 0.5f));
        ShadowShape shape = ShadowShape.Bsp(0x01000001, Vector3.Zero, Quaternion.Identity, 1, geometry);
        return ShadowPartBox.FromShape(shape, worldPosition ?? Origin, Quaternion.Identity);
    }

    private sealed class ReadTrackingSpheres(Sphere[] values) : IReadOnlyList<Sphere>
    {
        public List<int> Reads { get; } = [];
        public int Count => values.Length;
        public Sphere this[int index] { get { Reads.Add(index); return values[index]; } }
        public IEnumerator<Sphere> GetEnumerator() => ((IEnumerable<Sphere>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
