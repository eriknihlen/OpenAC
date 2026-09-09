using System.Collections.Generic;
using System.IO;
using System.Linq;
using AcDream.Content.Pak;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;

namespace AcDream.Content.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class PakEquivalenceTests {
    private static readonly uint[] KnownTrickyGfxObjIds = { 0x010002B4u, 0x010008A8u, 0x010014C3u };

    private static readonly uint[] SetupIds = { 0x020019FFu, 0x020005D8u, 0x020003F2u };

    private const uint HoltburgLandblock = 0xA9B40000u;

    [Fact]
    public void LiveExtraction_MatchesPakRoundTrip_OnFixtureIdSet() {
        var datDir = ContentConformanceDats.ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        using var datReaderWriter = new DatCollectionAdapter(dats);
        var logger = new TestConsoleLogger();

        var sideStaged = new List<ObjectMeshData>();
        var extractor = new MeshExtractor(datReaderWriter, logger, data => sideStaged.Add(data));

        var gfxObjIds = BuildGfxObjIdSet(dats);
        var setupIds = SetupIds.ToList();
        var envCellIds = BuildEnvCellIdSet(dats, HoltburgLandblock, minCount: 5);

        Assert.True(gfxObjIds.Count >= 10, $"fixture GfxObj id set unexpectedly small ({gfxObjIds.Count})");
        Assert.True(setupIds.Count >= 3, $"fixture Setup id set unexpectedly small ({setupIds.Count})");
        Assert.True(envCellIds.Count >= 5, $"fixture EnvCell id set unexpectedly small ({envCellIds.Count})");

        var work = new List<(PakAssetType Type, uint FileId, ulong ExtractorId, bool IsSetup)>();
        work.AddRange(gfxObjIds.Select(id => (PakAssetType.GfxObjMesh, id, (ulong)id, false)));
        work.AddRange(setupIds.Select(id => (PakAssetType.SetupMesh, id, (ulong)id, true)));
        work.AddRange(envCellIds.Select(id => (PakAssetType.EnvCellMesh, id, id | 0x1_0000_0000UL, false)));

        // ---- LIVE extraction (golden) ----
        var golden = new Dictionary<(PakAssetType, uint), ObjectMeshData>();
        var extractionFailures = new List<string>();
        foreach (var (type, fileId, extractorId, isSetup) in work) {
            var data = extractor.PrepareMeshData(extractorId, isSetup);
            if (data is null) {
                extractionFailures.Add($"{type} 0x{fileId:X8}: live extraction returned null");
                continue;
            }
            golden[(type, fileId)] = data;
        }

        Assert.True(extractionFailures.Count == 0,
            $"{extractionFailures.Count} fixture ids failed LIVE extraction (fixture assumption broke): " +
            string.Join(" | ", extractionFailures));

        // ---- bake the SAME ids to a temp pak ----
        var pakPath = Path.Combine(Path.GetTempPath(), $"acdream-equivtest-{System.Guid.NewGuid():N}.pak");
        try {
            var header = new PakHeader {
                FormatVersion = PakFormat.CurrentFormatVersion,
                PortalIteration = (uint)dats.Portal.Iteration!.CurrentIteration,
                CellIteration = (uint)dats.Cell.Iteration!.CurrentIteration,
                HighResIteration = (uint)dats.HighRes.Iteration!.CurrentIteration,
                LanguageIteration = (uint)dats.Local.Iteration!.CurrentIteration,
                BakeToolVersion = PakFormat.CurrentBakeToolVersion,
            };
            using (var writer = new PakWriter(pakPath, header)) {
                foreach (var ((type, fileId), data) in golden) {
                    writer.AddBlob(PakKey.Compose(type, fileId), data);
                }
                writer.Finish();
            }

            using var reader = new PakReader(pakPath);
            var mismatches = new List<string>();
            foreach (var ((type, fileId), expected) in golden) {
                var key = PakKey.Compose(type, fileId);
                if (!reader.TryReadObjectMeshData(key, out var actual)) {
                    mismatches.Add($"{type} 0x{fileId:X8}: pak read failed (missing or CRC mismatch)");
                    continue;
                }
                try {
                    ObjectMeshDataEquality.AssertEqual(expected, actual);
                }
                catch (Xunit.Sdk.XunitException ex) {
                    mismatches.Add($"{type} 0x{fileId:X8}: {ex.Message}");
                }
            }

            Assert.True(mismatches.Count == 0,
                $"{mismatches.Count}/{golden.Count} fixture ids mismatched between live extraction and pak round-trip:\n" +
                string.Join("\n", mismatches));
        }
        finally {
            if (File.Exists(pakPath)) File.Delete(pakPath);
        }
    }

    private static List<uint> BuildGfxObjIdSet(DatCollection dats) {
        var ids = new List<uint>(KnownTrickyGfxObjIds);
        var seen = new HashSet<uint>(ids);

        foreach (var id in dats.GetAllIdsOfType<GfxObj>()) {
            if (ids.Count >= 10) break;
            if (seen.Add(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>Walks the Holtburg landblock's LandBlockInfo.NumCells range (mirrors StipplingSurfaceEquivalenceTests' enumeration) to get at least <paramref name="minCount"/> real EnvCell ids.</summary>
    private static List<uint> BuildEnvCellIdSet(DatCollection dats, uint landblockId, int minCount) {
        var ids = new List<uint>();
        var lbInfo = dats.Get<LandBlockInfo>(landblockId | 0xFFFEu);
        Assert.True(lbInfo is not null, $"LandBlockInfo for landblock 0x{landblockId:X8} not found — fixture assumption broke");
        Assert.True(lbInfo!.NumCells >= minCount,
            $"landblock 0x{landblockId:X8} has only {lbInfo.NumCells} cells — fixture assumption broke (need >= {minCount})");

        uint firstCellId = landblockId | 0x0100u;
        for (uint offset = 0; offset < lbInfo.NumCells && ids.Count < minCount; offset++) {
            uint envCellId = firstCellId + offset;
            if (dats.Get<EnvCell>(envCellId) is not null) ids.Add(envCellId);
        }
        return ids;
    }
}
