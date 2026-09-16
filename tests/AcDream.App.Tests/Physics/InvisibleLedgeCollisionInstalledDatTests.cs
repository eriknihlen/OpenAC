using System.Collections.Immutable;
using System.Numerics;
using AcDream.App.Streaming;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Meshing;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter.DBObjs;
using Xunit.Abstractions;
using DatEnvCell = DatReaderWriter.DBObjs.EnvCell;
using DatEnvironment = DatReaderWriter.DBObjs.Environment;
using DatGfxObj = DatReaderWriter.DBObjs.GfxObj;
using DatSetup = DatReaderWriter.DBObjs.Setup;

namespace AcDream.App.Tests.Physics;

/// <summary>
/// Some interiors are built with invisible ledges: a placement whose only part
/// is an editor marker, which draws nothing and carries collision polygons. A
/// character stands on one of them in the original client, so hydration has to
/// keep the part and the published landblock has to collide with it.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class InvisibleLedgeCollisionInstalledDatTests
    : IClassFixture<InvisibleLedgeCollisionInstalledDatTests.Scene>
{
    private const uint LandblockId = 0x7E03FFFFu;
    private const int LandblockX = 0x7E;
    private const int LandblockY = 0x03;

    /// <summary>The reported interior cell.</summary>
    private const uint LedgeCell = 0x7E0307A3u;

    /// <summary>The placement that stands in the reported cell: one part, no lights.</summary>
    private const uint LedgePlacement = 0x02000C2Du;

    /// <summary>Its part: an editor marker with collision polygons.</summary>
    private const uint LedgePart = 0x010028C7u;

    private const uint MoverEntityId = 0x0007E031u;

    /// <summary>Where a character stands on the ledge, as the client reports it.</summary>
    private static readonly Vector3 Reported = new(310.353577f, -312.687317f, 6.005f);

    private readonly Scene _scene;
    private readonly ITestOutputHelper _output;

    public InvisibleLedgeCollisionInstalledDatTests(Scene scene, ITestOutputHelper output)
        => (_scene, _output) = (scene, output);

    [InstalledDatFact]
    public void TheReportedCellKeepsItsInvisibleCollisionPart()
    {
        WorldEntity[] hydrated = _scene.Entities
            .Where(entity => entity.SourceGfxObjOrSetupId == LedgePlacement
                && entity.ParentCellId == LedgeCell)
            .ToArray();

        WorldEntity ledge = Assert.Single(hydrated);
        _output.WriteLine(
            $"placement 0x{LedgePlacement:X8} at {ledge.Position} parts "
            + string.Join(',', ledge.MeshRefs.Select(part => $"0x{part.GfxObjId:X8}")));

        Assert.Contains(ledge.MeshRefs, part => part.GfxObjId == LedgePart);
        Assert.True(
            GfxObjDegradeResolver.IsRuntimeHiddenMarker(_scene.Dats, LedgePart),
            "the part draws nothing");
        Assert.True(
            _scene.HasCollision(LedgePart),
            "the part carries collision polygons");
    }

    [InstalledDatFact]
    public void EveryLedgePlacementInTheLandblockSurvivesHydration()
    {
        int authored = _scene.CountAuthoredPlacements(LedgePlacement);
        int hydrated = _scene.Entities
            .Count(entity => entity.SourceGfxObjOrSetupId == LedgePlacement);

        _output.WriteLine($"ledge placements: authored={authored} hydrated={hydrated}");
        Assert.True(authored > 0, "the installed content must author this placement");
        Assert.Equal(authored, hydrated);
    }

    [InstalledDatFact]
    public void ACharacterDroppedOntoTheLedgeComesToRestOnIt()
    {
        Vector3 from = Reported with { Z = Reported.Z + 1f };
        Vector3 target = Reported with { Z = Reported.Z - 1f };

        ResolveResult result = _scene.Engine.ResolveWithTransition(
            from,
            target,
            LedgeCell,
            _scene.Radius,
            _scene.Height,
            _scene.StepUp,
            _scene.StepDown,
            isOnGround: false,
            body: new PhysicsBody { Position = from, Orientation = Quaternion.Identity },
            moverFlags: ObjectInfoState.IsPlayer | ObjectInfoState.EdgeSlide,
            movingEntityId: MoverEntityId,
            sphereList: _scene.Spheres);

        _output.WriteLine(
            $"drop from={from} target={target} actual={result.Position} ok={result.Ok}");
        Assert.True(result.Ok);
        Assert.InRange(result.Position.Z, Reported.Z - 0.05f, Reported.Z + 0.05f);
    }

    public sealed class Scene : IDisposable
    {
        private const uint HumanSetup = 0x02000001u;

        private readonly BoundedTestDatCollection _dat;
        private readonly DatCollisionSource _collision;

        public Scene()
        {
            string? directory = InstalledDatTestPath.Resolve();
            Assert.True(
                Directory.Exists(directory),
                "An installed content directory is required.");
            _dat = new BoundedTestDatCollection(directory!);
            var bounded = (IDatReaderWriter)_dat;
            Dats = bounded;
            float[] heights = Assert.IsType<Region>(bounded.Get<Region>(0x13000000u))
                .LandDefs.LandHeightTable;
            _collision = new DatCollisionSource(bounded);

            PreparedCollisionReadResult<FlatSetupCollision> mover =
                _collision.ReadSetupCollision(HumanSetup);
            Assert.Equal(PreparedAssetReadStatus.Loaded, mover.Status);
            FlatSetupCollision body = Assert.IsType<FlatSetupCollision>(mover.Data);
            Assert.Equal(2, body.Spheres.Length);
            Spheres = body.Spheres;
            Radius = body.Spheres[0].Radius;
            Height = body.Height;
            StepUp = body.StepUpHeight;
            StepDown = body.StepDownHeight;

            var factory = new LandblockBuildFactory(
                bounded,
                _collision,
                new object(),
                heights);
            LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(
                new LandblockBuildRequest(
                    LandblockId,
                    LandblockStreamJobKind.LoadNear,
                    Generation: 1,
                    new LandblockBuildOrigin(LandblockX, LandblockY))));
            LandblockCollisionBuild collisions =
                Assert.IsType<LandblockCollisionBuild>(build.Collisions);
            Entities = build.Landblock.Entities;

            Cache = PhysicsDataCache.CreateProduction();
            Engine = new PhysicsEngine { DataCache = Cache };
            TerrainSurface terrain = LandblockPhysicsContentBuilder.BuildTerrainSurface(
                build.Landblock,
                heights);
            var cellSurfaces = new List<CellSurface>();
            var portalPlanes = new List<PortalPlane>();
            LandblockPhysicsContentBuilder.PublishPreparedCells(
                Cache,
                build.Landblock,
                collisions,
                Vector3.Zero,
                cellSurfaces,
                portalPlanes);
            LandblockPhysicsContentBuilder.CacheBuildings(
                Cache,
                build.Landblock,
                terrain,
                Vector3.Zero);
            LandblockPhysicsContentBuilder.CachePreparedObjects(Cache, collisions);
            Engine.AddLandblock(
                build.LandblockId,
                terrain,
                cellSurfaces,
                portalPlanes,
                0f,
                0f);
            _ = LandblockPhysicsContentBuilder.PublishStaticCollision(
                Engine,
                Cache,
                build.Landblock,
                collisions,
                Vector3.Zero);
        }

        internal IDatReaderWriter Dats { get; }

        internal IReadOnlyList<WorldEntity> Entities { get; }

        internal PhysicsDataCache Cache { get; }

        internal PhysicsEngine Engine { get; }

        internal ImmutableArray<FlatCollisionSphere> Spheres { get; }

        internal float Radius { get; }

        internal float Height { get; }

        internal float StepUp { get; }

        internal float StepDown { get; }

        /// <summary>Whether a model carries collision polygons of its own.</summary>
        internal bool HasCollision(uint gfxObjId) =>
            Dats.Get<DatGfxObj>(gfxObjId) is { } gfx
            && gfx.Flags.HasFlag(DatReaderWriter.Enums.GfxObjFlags.HasPhysics);

        /// <summary>How many times the landblock's cells place this object.</summary>
        internal int CountAuthoredPlacements(uint setupId)
        {
            LandBlockInfo info = Assert.IsType<LandBlockInfo>(
                Dats.Get<LandBlockInfo>((LandblockId & 0xFFFF0000u) | 0xFFFEu));
            uint first = (LandblockId & 0xFFFF0000u) | 0x0100u;
            int count = 0;
            for (uint offset = 0; offset < info.NumCells; offset++)
            {
                if (Dats.Get<DatEnvCell>(first + offset) is not { } cell)
                    continue;
                foreach (DatReaderWriter.Types.Stab stab in cell.StaticObjects)
                {
                    if (stab.Id == setupId)
                        count++;
                }
            }
            return count;
        }

        public void Dispose()
        {
            _collision.Dispose();
            _dat.Dispose();
        }
    }

    /// <summary>Prepared collision read straight from the installed content.</summary>
    private sealed class DatCollisionSource : IPreparedCollisionSource
    {
        private readonly IDatReaderWriter _dats;

        internal DatCollisionSource(IDatReaderWriter dats) => _dats = dats;

        public PreparedCollisionSourceStats CollisionStats => default;

        public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId) =>
            type switch
            {
                PakAssetType.GfxObjCollision => Presence(_dats.Get<DatGfxObj>(sourceFileId)),
                PakAssetType.SetupCollision => Presence(_dats.Get<DatSetup>(sourceFileId)),
                PakAssetType.CellStructureCollision =>
                    TryResolveCell(sourceFileId, out _, out _)
                        ? PreparedAssetPresence.Available
                        : PreparedAssetPresence.Missing,
                PakAssetType.EnvCellTopology =>
                    TryResolveCell(sourceFileId, out _, out _)
                        ? PreparedAssetPresence.Available
                        : PreparedAssetPresence.Missing,
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            };

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _dats.Get<DatGfxObj>(sourceFileId) is { } value
                ? PreparedCollisionReadResult<FlatGfxObjCollisionAsset>.Loaded(
                    FlatCollisionAssetBuilder.FlattenGfxObj(value))
                : PreparedCollisionReadResult<FlatGfxObjCollisionAsset>.Missing;
        }

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _dats.Get<DatSetup>(sourceFileId) is { } value
                ? PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                    FlatCollisionAssetBuilder.FlattenSetup(value))
                : PreparedCollisionReadResult<FlatSetupCollision>.Missing;
        }

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset>
            ReadCellStructureCollision(
                uint sourceFileId,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TryResolveCell(sourceFileId, out _, out DatReaderWriter.Types.CellStruct structure)
                ? PreparedCollisionReadResult<FlatCellStructureCollisionAsset>.Loaded(
                    FlatCollisionAssetBuilder.FlattenCellStructure(structure))
                : PreparedCollisionReadResult<FlatCellStructureCollisionAsset>.Missing;
        }

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryResolveCell(sourceFileId, out DatEnvCell? cell, out DatReaderWriter.Types.CellStruct structure))
                return PreparedCollisionReadResult<FlatEnvCellTopology>.Missing;
            FlatCellStructureCollisionAsset flat =
                FlatCollisionAssetBuilder.FlattenCellStructure(structure);
            return PreparedCollisionReadResult<FlatEnvCellTopology>.Loaded(
                FlatCollisionAssetBuilder.FlattenEnvCellTopology(
                    sourceFileId,
                    cell!,
                    flat.PortalPolygons));
        }

        public void Dispose()
        {
        }

        private static PreparedAssetPresence Presence(object? value) =>
            value is not null
                ? PreparedAssetPresence.Available
                : PreparedAssetPresence.Missing;

        private bool TryResolveCell(
            uint sourceFileId,
            out DatEnvCell? cell,
            out DatReaderWriter.Types.CellStruct structure)
        {
            cell = _dats.Get<DatEnvCell>(sourceFileId);
            if (cell is not null
                && _dats.Get<DatEnvironment>(0x0D000000u | cell.EnvironmentId) is { } environment
                && environment.Cells.TryGetValue(cell.CellStructure, out DatReaderWriter.Types.CellStruct? found)
                && found is not null)
            {
                structure = found;
                return true;
            }
            structure = null!;
            return false;
        }
    }
}
