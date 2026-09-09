using System.Collections.Generic;
using System.IO;
using AcDream.Core.Physics;
using AcDream.Core.Terrain;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Options;
using Xunit;
using Xunit.Abstractions;
using Env = System.Environment;

namespace AcDream.Core.Tests.Terrain;

[Trait("Lane", "InstalledDat")]
public class TerrainModernConformanceTests
{
    private readonly ITestOutputHelper _out;

    public TerrainModernConformanceTests(ITestOutputHelper output) => _out = output;

    private static readonly (string name, uint lbX, uint lbY)[] RepresentativeLandblocks =
    {
        ("Holtburg flat 0xA9B0",     0xA9, 0xB0),
        ("Holtburg sloped 0xA9B1",   0xA9, 0xB1),
        ("Foundry-area 0x8080",      0x80, 0x80),
        ("Cragstone 0xCB99",         0xCB, 0x99),
        ("Direlands sample 0xC040",  0xC0, 0x40),
        ("MapOrigin 0x0000",         0x00, 0x00),
        ("Mid-map 0x7F7F",           0x7F, 0x7F),
        ("MapCorner 0xFEFE",         0xFE, 0xFE),
        ("Subway outdoor 0x0185",    0x01, 0x85),
        ("North continent 0x4D96",   0x4D, 0x96),
    };

    [Fact]
    public void VisualMeshZ_AgreesWith_PhysicsZ_WithinOneMillimeter()
    {
        var datDir = Env.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(Env.GetFolderPath(Env.SpecialFolder.UserProfile),
                            "Documents", "Asheron's Call");
        if (!Directory.Exists(datDir))
        {
            _out.WriteLine($"SKIP: dat directory not found at {datDir}");
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");
        }

        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var region = dats.Get<Region>(0x13000000u);
        Assert.NotNull(region);
        var heightTable = region.LandDefs.LandHeightTable;
        Assert.NotNull(heightTable);
        Assert.True(heightTable.Length >= 256, "heightTable must have at least 256 entries");

        var ctx = new TerrainBlendingContext(
            TerrainTypeToLayer: new Dictionary<uint, byte>(),
            RoadLayer: SurfaceInfo.None,
            CornerAlphaLayers: Array.Empty<byte>(),
            SideAlphaLayers:   Array.Empty<byte>(),
            RoadAlphaLayers:   Array.Empty<byte>(),
            CornerAlphaTCodes: Array.Empty<uint>(),
            SideAlphaTCodes:   Array.Empty<uint>(),
            RoadAlphaRCodes:   Array.Empty<uint>());

        long totalSamples = 0;
        long totalLandblocksTested = 0;
        double maxDelta = 0;
        (string name, uint lbX, uint lbY, float lx, float ly, float meshZ, float physicsZ) worstCase = default;

        var rng = new Random(42);

        foreach (var (name, lbX, lbY) in RepresentativeLandblocks)
        {
            uint landblockId = (lbX << 24) | (lbY << 16) | 0xFFFFu;
            var landblock = dats.Get<LandBlock>(landblockId);
            if (landblock is null)
            {
                _out.WriteLine($"  skipped {name}: dat not found (probably water-only)");
                continue;
            }
            totalLandblocksTested++;

            var surfaceCache = new Dictionary<uint, SurfaceInfo>();
            var meshData = LandblockMesh.Build(landblock, lbX, lbY, heightTable, ctx, surfaceCache);

            for (int s = 0; s < 100; s++)
            {
                float lx = (float)rng.NextDouble() * 191.975f;
                float ly = (float)rng.NextDouble() * 191.975f;

                float meshZ = SampleMeshZ(meshData, lx, ly);
                float physicsZ = TerrainSurface.SampleZFromHeightmap(
                    landblock.Height, heightTable, lbX, lbY, lx, ly);

                double delta = Math.Abs(meshZ - physicsZ);
                if (delta > maxDelta)
                {
                    maxDelta = delta;
                    worstCase = (name, lbX, lbY, lx, ly, meshZ, physicsZ);
                }
                totalSamples++;
                Assert.True(delta < 0.001,
                    $"Mesh Z disagrees with physics Z at lb=0x{lbX:X2}{lbY:X2} ({name}) " +
                    $"local=({lx:F2},{ly:F2}): meshZ={meshZ:F4} physicsZ={physicsZ:F4} delta={delta:F4}m");
            }
        }

        _out.WriteLine($"=== Phase N.5b conformance sweep ===");
        _out.WriteLine($"Landblocks tested: {totalLandblocksTested}/{RepresentativeLandblocks.Length}");
        _out.WriteLine($"Total samples:     {totalSamples}");
        _out.WriteLine($"Max |delta|:       {maxDelta * 1000:F4} mm  (tolerance: 1.0 mm)");
        if (totalSamples > 0)
            _out.WriteLine($"Worst case: {worstCase.name} local=({worstCase.lx:F2},{worstCase.ly:F2}) " +
                           $"meshZ={worstCase.meshZ:F4} physicsZ={worstCase.physicsZ:F4}");

        Assert.True(totalLandblocksTested >= 5,
            $"Expected at least 5 representative landblocks loadable; got {totalLandblocksTested}.");
    }

    private static float SampleMeshZ(LandblockMeshData mesh, float lx, float ly)
    {
        for (int triBase = 0; triBase < mesh.Indices.Length; triBase += 3)
        {
            var v0 = mesh.Vertices[mesh.Indices[triBase + 0]];
            var v1 = mesh.Vertices[mesh.Indices[triBase + 1]];
            var v2 = mesh.Vertices[mesh.Indices[triBase + 2]];

            float denom = (v1.Position.Y - v2.Position.Y) * (v0.Position.X - v2.Position.X)
                        + (v2.Position.X - v1.Position.X) * (v0.Position.Y - v2.Position.Y);
            if (Math.Abs(denom) < 1e-9f) continue;

            float a = ((v1.Position.Y - v2.Position.Y) * (lx - v2.Position.X)
                     + (v2.Position.X - v1.Position.X) * (ly - v2.Position.Y)) / denom;
            float b = ((v2.Position.Y - v0.Position.Y) * (lx - v2.Position.X)
                     + (v0.Position.X - v2.Position.X) * (ly - v2.Position.Y)) / denom;
            float c = 1f - a - b;

            // Inside test with epsilon for boundary stability — points that
            // land exactly on a shared edge between two triangles still
            // resolve, picking whichever the loop hits first (Z agrees on
            // the seam either way).
            const float eps = 1e-4f;
            if (a >= -eps && b >= -eps && c >= -eps)
                return a * v0.Position.Z + b * v1.Position.Z + c * v2.Position.Z;
        }

        // Should not happen for valid mesh + in-bounds (lx, ly).
        throw new InvalidOperationException(
            $"No triangle found containing local=({lx:F2},{ly:F2}); mesh has {mesh.Indices.Length / 3} triangles.");
    }
}
