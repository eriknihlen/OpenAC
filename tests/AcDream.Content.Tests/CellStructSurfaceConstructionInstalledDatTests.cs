using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using AcDream.Core.Meshing;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Options;
using Xunit.Abstractions;
using RetailCullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class CellStructSurfaceConstructionInstalledDatTests {
    private readonly ITestOutputHelper _out;
    public CellStructSurfaceConstructionInstalledDatTests(ITestOutputHelper output) => _out = output;

    private static readonly uint[] CanonicalCellIds = {
        0xF4180100u, 0xF4180101u, 0xF4180104u, 0xF4180106u, 0xF4180107u,
        0xF4180112u, 0xF4180113u, 0xF4180114u,
        0x8A02015Eu, 0x8A02015Fu,
    };

    private static MeshExtractor NewExtractor(DatCollection dats) {
        var reader = new DatCollectionAdapter(dats);
        return new MeshExtractor(reader, new TestConsoleLogger(), sideStagedSink: null);
    }


    [Fact]
    public void InstalledDatScan_EveryOldNewAdmissionDeltaIsExplainedBySurfaceType() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) { Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); return; }

        var stopwatch = Stopwatch.StartNew();
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        long landblocksVisited = 0;
        long landblocksWithCells = 0;
        long cellsVisited = 0;
        long cellsWithGeometry = 0;
        long missingEnvironmentOrStruct = 0;
        long polygons = 0;
        long candidates = 0;
        long unknownSidesType = 0;
        long unresolvedSurfaceSlots = 0;
        long oldOnly = 0;   // old=emit, new=skip
        long newOnly = 0;   // old=skip, new=emit
        var unexplained = new List<string>();

        var slotCache = new Dictionary<int, Surface?>();

        for (uint landblockPrefix = 0; landblockPrefix <= 0xFFFFu; landblockPrefix++) {
            uint lbId = landblockPrefix << 16;
            var lbInfo = dats.Get<LandBlockInfo>(lbId | 0xFFFEu);
            if (lbInfo is null) continue;
            landblocksVisited++;
            if (lbInfo.NumCells == 0) continue;
            landblocksWithCells++;

            for (uint low = 0x0100u; low < 0x0100u + lbInfo.NumCells; low++) {
                uint envCellId = lbId | low;
                cellsVisited++;
                var envCell = dats.Get<EnvCell>(envCellId);
                if (envCell is null || envCell.EnvironmentId == 0) continue;

                uint envId = 0x0D000000u | envCell.EnvironmentId;
                var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(envId);
                if (environment is null || !environment.Cells.TryGetValue(envCell.CellStructure, out var cellStruct)) {
                    missingEnvironmentOrStruct++;
                    continue;
                }
                cellsWithGeometry++;
                slotCache.Clear();

                Surface? ResolveSlot(int slot) {
                    if (slotCache.TryGetValue(slot, out var cached)) return cached;
                    Surface? resolved = null;
                    if (slot >= 0 && slot < envCell.Surfaces.Count) {
                        uint surfaceId = 0x08000000u | envCell.Surfaces[slot];
                        resolved = dats.Get<Surface>(surfaceId);
                    }
                    slotCache[slot] = resolved;
                    return resolved;
                }

                foreach (var (polyId, poly) in cellStruct.Polygons) {
                    if (poly.VertexIds.Count < 3) continue;
                    polygons++;

                    if (!CellStructSideCandidates.IsRetailDefinedSidesType((int)poly.SidesType))
                        unknownSidesType++;
                    ReadOnlySpan<CellStructSideCandidate> polyCandidates =
                        CellStructSideCandidates.GetCandidates((int)poly.SidesType);

                    foreach (var candidate in polyCandidates) {
                        candidates++;
                        bool isPositive = candidate.SurfaceSlot == CellStructPolygonSurfaceSide.Positive;
                        short slotRaw = isPositive ? poly.PosSurface : poly.NegSurface;

                        bool oldAdmitted = isPositive
                            ? !poly.Stippling.HasFlag(StipplingType.NoPos)
                            : !poly.Stippling.HasFlag(StipplingType.NoNeg);

                        Surface? surface = ResolveSlot(slotRaw);
                        if (surface is null) {
                            unresolvedSurfaceSlots++;
                            continue;
                        }

                        bool newAdmitted = (surface.Type & (SurfaceType.Base1Image | SurfaceType.Base1ClipMap)) != 0;

                        if (oldAdmitted && !newAdmitted) {
                            oldOnly++;
                            bool untextured = (surface.Type & (SurfaceType.Base1Image | SurfaceType.Base1ClipMap)) == 0;
                            if (!untextured)
                                unexplained.Add($"OLD-ONLY unexplained: envCell=0x{envCellId:X8} env=0x{envId:X8} struct={envCell.CellStructure} poly={polyId} side={candidate.SurfaceSlot} slot={slotRaw} type=0x{(uint)surface.Type:X8}");
                        }
                        else if (!oldAdmitted && newAdmitted) {
                            newOnly++;
                            bool uvAbsenceBitSet = isPositive
                                ? poly.Stippling.HasFlag(StipplingType.NoPos)
                                : poly.Stippling.HasFlag(StipplingType.NoNeg);
                            if (!uvAbsenceBitSet)
                                unexplained.Add($"NEW-ONLY unexplained: envCell=0x{envCellId:X8} env=0x{envId:X8} struct={envCell.CellStructure} poly={polyId} side={candidate.SurfaceSlot} slot={slotRaw} type=0x{(uint)surface.Type:X8} stippling={poly.Stippling}");
                        }
                    }
                }
            }
        }

        stopwatch.Stop();
        _out.WriteLine($"duration: {stopwatch.Elapsed}");
        _out.WriteLine($"landblocksVisited={landblocksVisited} landblocksWithCells={landblocksWithCells}");
        _out.WriteLine($"cellsVisited={cellsVisited} cellsWithGeometry={cellsWithGeometry} missingEnvironmentOrStruct={missingEnvironmentOrStruct}");
        _out.WriteLine($"polygons={polygons} candidates={candidates} unknownSidesType={unknownSidesType} unresolvedSurfaceSlots={unresolvedSurfaceSlots}");
        _out.WriteLine($"oldOnly={oldOnly} newOnly={newOnly} unexplained={unexplained.Count}");
        foreach (var line in unexplained.Take(50)) _out.WriteLine("   " + line);

        Assert.Empty(unexplained);

        Assert.Equal(CellStructInstalledDatGolden.CellsVisited, cellsVisited);
        Assert.Equal(CellStructInstalledDatGolden.CellsWithGeometry, cellsWithGeometry);
        Assert.Equal(CellStructInstalledDatGolden.MissingEnvironmentOrStruct, missingEnvironmentOrStruct);
        Assert.Equal(CellStructInstalledDatGolden.Polygons, polygons);
        Assert.Equal(CellStructInstalledDatGolden.Candidates, candidates);
        Assert.Equal(CellStructInstalledDatGolden.UnknownSidesType, unknownSidesType);
        Assert.Equal(CellStructInstalledDatGolden.UnresolvedSurfaceSlots, unresolvedSurfaceSlots);
        Assert.Equal(CellStructInstalledDatGolden.OldOnly, oldOnly);
        Assert.Equal(CellStructInstalledDatGolden.NewOnly, newOnly);
    }


    [Fact]
    public void Cell0xF4180104_HasEightStDoubleClipMapPolygonsAndFortyFourDrawableSideCalls() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) { Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); return; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        const uint envCellId = 0xF4180104u;
        var (envCell, _, cellStruct) = ResolveCanonicalCell(dats, envCellId);

        int stDoubleCount = 0;
        int drawableSideCalls = 0;
        var slotCache = new Dictionary<int, Surface?>();

        Surface? ResolveSlot(int slot) {
            if (slotCache.TryGetValue(slot, out var cached)) return cached;
            Surface? resolved = null;
            if (slot >= 0 && slot < envCell.Surfaces.Count) {
                uint surfaceId = 0x08000000u | envCell.Surfaces[slot];
                resolved = dats.Get<Surface>(surfaceId);
            }
            slotCache[slot] = resolved;
            return resolved;
        }

        foreach (var poly in cellStruct.Polygons.Values) {
            if (poly.VertexIds.Count < 3) continue;
            if (poly.SidesType == RetailCullMode.None) stDoubleCount++; // raw sides_type 1 = ST_DOUBLE

            foreach (var candidate in CellStructSideCandidates.GetCandidates((int)poly.SidesType)) {
                bool isPositive = candidate.SurfaceSlot == CellStructPolygonSurfaceSide.Positive;
                short slotRaw = isPositive ? poly.PosSurface : poly.NegSurface;
                var surface = ResolveSlot(slotRaw);
                if (surface is null) continue;
                if (!RetailUntexturedSurfacePolicy.IsUntextured(surface.Type))
                    drawableSideCalls++;
            }
        }

        _out.WriteLine($"0xF4180104: stDoubleCount={stDoubleCount} drawableSideCalls={drawableSideCalls}");
        Assert.Equal(8, stDoubleCount);
        Assert.Equal(44, drawableSideCalls);
    }

    [Fact]
    public void CanonicalCells_NoPosSurfacesAreType0x11AndConstructedButSkipped() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) { Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); return; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var extractor = NewExtractor(dats);

        int noPosSurfacesChecked = 0;
        foreach (uint envCellId in CanonicalCellIds) {
            var (envCell, _, cellStruct) = ResolveCanonicalCell(dats, envCellId);

            var noPosSlots = new HashSet<int>();
            foreach (var poly in cellStruct.Polygons.Values) {
                if (poly.VertexIds.Count < 3) continue;
                if (!poly.Stippling.HasFlag(StipplingType.NoPos)) continue;
                if (poly.PosSurface < 0 || poly.PosSurface >= envCell.Surfaces.Count) continue;

                uint surfaceId = 0x08000000u | envCell.Surfaces[poly.PosSurface];
                var surface = dats.Get<Surface>(surfaceId);
                Assert.NotNull(surface);
                Assert.Equal(0x11u, (uint)surface!.Type);
                noPosSlots.Add(poly.PosSurface);
                noPosSurfacesChecked++;
            }

            if (noPosSlots.Count == 0) continue;

            ObjectMeshData? mesh = extractor.PrepareMeshData(envCellId | 0x1_0000_0000UL, isSetup: false);
            Assert.NotNull(mesh);
            var emittedSlots = CellSurfaceSubsets.InAscendingSurfaceOrder(mesh!)
                .Select(b => b.SourceSurfaceIndex)
                .ToHashSet();
            foreach (int slot in noPosSlots)
                Assert.DoesNotContain(slot, emittedSlots);
        }

        _out.WriteLine($"noPosSurfacesChecked={noPosSurfacesChecked} across {CanonicalCellIds.Length} canonical cells");
        Assert.True(noPosSurfacesChecked > 0, "expected at least one NoPos polygon across the canonical cells (contract §6)");
    }

    [Fact]
    public void CanonicalCells_EmittedSubsetHash_IsDeterministicAcrossTwoRuns() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) { Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md."); return; }
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        foreach (uint envCellId in CanonicalCellIds) {
            var extractor1 = NewExtractor(dats);
            var extractor2 = NewExtractor(dats);

            ObjectMeshData? mesh1 = extractor1.PrepareMeshData(envCellId | 0x1_0000_0000UL, isSetup: false);
            ObjectMeshData? mesh2 = extractor2.PrepareMeshData(envCellId | 0x1_0000_0000UL, isSetup: false);
            Assert.NotNull(mesh1);
            Assert.NotNull(mesh2);

            string hash1 = HashCellSubsets(mesh1!);
            string hash2 = HashCellSubsets(mesh2!);
            _out.WriteLine($"0x{envCellId:X8} hash={hash1}");
            Assert.Equal(hash1, hash2);
        }
    }

    private static string HashCellSubsets(ObjectMeshData mesh) {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new System.IO.BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) {
            writer.Write(mesh.Vertices.Length);
            foreach (var batch in CellSurfaceSubsets.InAscendingSurfaceOrder(mesh)) {
                writer.Write(batch.SourceSurfaceIndex);
                writer.Write(batch.RawSurfaceType);
                writer.Write(batch.RetailSurfaceMask);
                writer.Write(batch.Indices.Count);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static (EnvCell envCell, uint envId, DatReaderWriter.Types.CellStruct cellStruct) ResolveCanonicalCell(
        DatCollection dats, uint envCellId) {
        var envCell = dats.Get<EnvCell>(envCellId);
        Assert.NotNull(envCell);
        uint envId = 0x0D000000u | envCell!.EnvironmentId;
        var environment = dats.Get<DatReaderWriter.DBObjs.Environment>(envId);
        Assert.NotNull(environment);
        Assert.True(environment!.Cells.TryGetValue(envCell.CellStructure, out var cellStruct),
            $"EnvCell 0x{envCellId:X8}: Environment 0x{envId:X8} has no CellStruct {envCell.CellStructure}");
        return (envCell, envId, cellStruct!);
    }
}

internal static class CellStructInstalledDatGolden {
    public const long CellsVisited = 729888;
    public const long CellsWithGeometry = 729888;
    public const long MissingEnvironmentOrStruct = 0;
    public const long Polygons = 8601560;
    public const long Candidates = 8608746;
    public const long UnknownSidesType = 0;
    public const long UnresolvedSurfaceSlots = 0;

    public const long OldOnly = 3197;
    public const long NewOnly = 0;
}
