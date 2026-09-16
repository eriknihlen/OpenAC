using System.Collections.Immutable;
using AcDream.App.Streaming;
using AcDream.App.Tests.Rendering;
using AcDream.App.Tests.UI.Layout;
using AcDream.Content;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using AcDream.Core.World;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using DatReaderWriter.Types;
using Xunit.Abstractions;

namespace AcDream.App.Tests.Streaming;

/// <summary>
/// An interior placement whose whole purpose is an effect is authored as one
/// editor-marker part with a default script and no lights. Hydration used to
/// drop it for having no drawable geometry, which silently removed the effect
/// the placement exists to produce. These pins keep such placements alive
/// through the production interior hydrator against the installed content.
/// </summary>
[Trait("Lane", "InstalledDat")]
public sealed class InteriorEffectPlacementHydrationInstalledDatTests
{
    private const uint LandblockId = 0x0108FFFFu;
    private const int LandblockX = 0x01;
    private const int LandblockY = 0x08;

    /// <summary>The reported interior cell.</summary>
    private const uint ReportedCell = 0x0108020Du;

    /// <summary>
    /// The ground-haze placement inside the reported cell: one editor-marker
    /// part, no lights, a default script that creates the emitter.
    /// </summary>
    private const uint GroundHazePlacement = 0x020003FFu;

    /// <summary>
    /// The glow placement this content pairs with each lit wall fixture. It
    /// has the same authoring shape, and it is what paints a fixture's light
    /// onto the surrounding walls and floor.
    /// </summary>
    private const uint FixtureGlowPlacement = 0x020005B7u;

    private readonly ITestOutputHelper _output;

    public InteriorEffectPlacementHydrationInstalledDatTests(ITestOutputHelper output) =>
        _output = output;

    [InstalledDatFact]
    public void TheReportedCellKeepsItsGroundHazePlacement()
    {
        using var scene = new Scene();

        WorldEntity[] hydrated = scene.Entities
            .Where(e => e.SourceGfxObjOrSetupId == GroundHazePlacement
                && e.ParentCellId == ReportedCell)
            .ToArray();

        int authored = scene.CountAuthoredPlacements(GroundHazePlacement, ReportedCell);
        _output.WriteLine(
            $"cell 0x{ReportedCell:X8}: authored={authored} hydrated={hydrated.Length}");

        Assert.Equal(1, authored);
        WorldEntity kept = Assert.Single(hydrated);

        // The placement draws nothing; it exists to run its script.
        Assert.Empty(kept.MeshRefs);
        Assert.NotEqual(0u, scene.DefaultScriptOf(GroundHazePlacement));
    }

    [InstalledDatFact]
    public void EveryFixtureGlowPlacementInTheLandblockSurvivesHydration()
    {
        using var scene = new Scene();

        int authored = scene.CountAuthoredPlacements(FixtureGlowPlacement, cellId: null);
        int hydrated = scene.Entities
            .Count(e => e.SourceGfxObjOrSetupId == FixtureGlowPlacement);

        _output.WriteLine($"fixture glow: authored={authored} hydrated={hydrated}");
        Assert.True(authored > 0, "the installed content must author this placement");
        Assert.Equal(authored, hydrated);
    }

    [InstalledDatFact]
    [Trait("Purpose", "Diagnostic")]
    public void ReportTheReportedCellContent()
    {
        using var scene = new Scene();
        var cell = Assert.IsType<EnvCell>(scene.Dats.Get<EnvCell>(ReportedCell));
        _output.WriteLine(
            $"cell 0x{ReportedCell:X8} env=0x{cell.EnvironmentId:X4} statics={cell.StaticObjects.Count}");
        foreach (Stab stab in cell.StaticObjects)
        {
            if (!scene.Dats.Portal.TryGet<Setup>(stab.Id, out Setup? setup) || setup is null)
            {
                _output.WriteLine($"  0x{stab.Id:X8} at {stab.Frame.Origin} (no setup)");
                continue;
            }
            _output.WriteLine(
                $"  0x{stab.Id:X8} at {stab.Frame.Origin} parts={setup.Parts.Count} " +
                $"lights={setup.Lights.Count} script=0x{setup.DefaultScript.DataId:X8} " +
                $"scriptTable=0x{(uint)setup.DefaultScriptTable:X8}");
        }
    }

    private sealed class Scene : IDisposable
    {
        internal DatCollection Dats { get; }
        internal IReadOnlyList<WorldEntity> Entities { get; }

        internal Scene()
        {
            string? directory = InstalledDatTestPath.Resolve();
            Assert.True(
                Directory.Exists(directory),
                "An installed content directory is required.");
            Dats = new DatCollection(directory!, DatAccessType.Read);
            var adapter = new DatCollectionAdapter(Dats);
            float[] heights = Assert.IsType<Region>(adapter.Get<Region>(0x13000000u))
                .LandDefs.LandHeightTable;
            var factory = new LandblockBuildFactory(
                adapter,
                new EmptyCollisionSource(),
                new object(),
                heights);
            LandblockBuild build = Assert.IsType<LandblockBuild>(factory.Build(
                new LandblockBuildRequest(
                    LandblockId,
                    LandblockStreamJobKind.LoadNear,
                    Generation: 1,
                    new LandblockBuildOrigin(LandblockX, LandblockY))));
            Entities = build.Landblock.Entities;
        }

        internal uint DefaultScriptOf(uint setupId) =>
            Dats.Portal.TryGet<Setup>(setupId, out Setup? setup) && setup is not null
                ? setup.DefaultScript.DataId
                : 0u;

        /// <summary>How many times the landblock cells place this object.</summary>
        internal int CountAuthoredPlacements(uint setupId, uint? cellId)
        {
            var info = Assert.IsType<LandBlockInfo>(
                Dats.Get<LandBlockInfo>((LandblockId & 0xFFFF0000u) | 0xFFFEu));
            uint first = (LandblockId & 0xFFFF0000u) | 0x0100u;
            int count = 0;
            for (uint offset = 0; offset < info.NumCells; offset++)
            {
                uint id = first + offset;
                if (cellId is { } only && id != only)
                    continue;
                if (!Dats.Cell.TryGet<EnvCell>(id, out EnvCell? cell) || cell is null)
                    continue;
                foreach (Stab stab in cell.StaticObjects)
                    if (stab.Id == setupId)
                        count++;
            }
            return count;
        }

        public void Dispose() => Dats.Dispose();
    }

    /// <summary>
    /// Satisfies the near-tier collision closure with empty prepared assets:
    /// these pins are about which placements survive hydration, not about
    /// collision content.
    /// </summary>
    private sealed class EmptyCollisionSource : IPreparedCollisionSource
    {
        private static readonly FlatPhysicsBsp EmptyPhysics = new(
            -1,
            ImmutableArray<FlatPhysicsBspNode>.Empty,
            ImmutableArray<int>.Empty,
            FlatPolygonTable.Empty);

        public PreparedAssetPresence ProbeCollision(PakAssetType type, uint sourceFileId)
            => PreparedAssetPresence.Available;

        public PreparedCollisionReadResult<FlatGfxObjCollisionAsset> ReadGfxObjCollision(
            uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<FlatGfxObjCollisionAsset>.Loaded(
                new FlatGfxObjCollisionAsset(EmptyPhysics, null, null));

        public PreparedCollisionReadResult<FlatSetupCollision> ReadSetupCollision(
            uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<FlatSetupCollision>.Loaded(
                new FlatSetupCollision(
                    ImmutableArray<FlatCollisionCylinder>.Empty,
                    ImmutableArray<FlatCollisionSphere>.Empty,
                    0f, 0f, 0f, 0f));

        public PreparedCollisionReadResult<FlatCellStructureCollisionAsset> ReadCellStructureCollision(
            uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<FlatCellStructureCollisionAsset>.Loaded(
                new FlatCellStructureCollisionAsset(
                    EmptyPhysics,
                    new FlatCellContainmentBsp(-1, ImmutableArray<FlatCellBspNode>.Empty),
                    FlatPolygonTable.Empty));

        public PreparedCollisionReadResult<FlatEnvCellTopology> ReadEnvCellTopology(
            uint sourceFileId, CancellationToken cancellationToken = default)
            => PreparedCollisionReadResult<FlatEnvCellTopology>.Loaded(
                new FlatEnvCellTopology(
                    ImmutableArray<FlatEnvCellPortal>.Empty,
                    ImmutableArray<uint>.Empty,
                    seenOutside: false));

        public PreparedCollisionSourceStats CollisionStats => default;

        public void Dispose()
        {
        }
    }
}
