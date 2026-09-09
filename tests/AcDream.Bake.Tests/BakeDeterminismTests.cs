using System;
using System.Collections.Generic;
using System.IO;
using AcDream.Bake;
using AcDream.Content.Pak;

namespace AcDream.Bake.Tests;

[Trait("Lane", "InstalledDat")]
public sealed class BakeDeterminismTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string NewTempPakPath() {
        var path = Path.Combine(Path.GetTempPath(), $"acdream-baketest-{Guid.NewGuid():N}.pak");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var f in _tempFiles) {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private static string? ResolveDatDir() {
        var fromEnv = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        var def = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "Asheron's Call");
        return Directory.Exists(def) ? def : null;
    }

    [Fact]
    public void Bake_SameIdSet_DifferentThreadCounts_ByteIdenticalPaks() {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var ids = new HashSet<uint> {
            0x01000001u, 0x010002B4u, 0x010008A8u, 0x010014C3u,
            0x02000001u, 0x020019FFu, 0x020005D8u,
            0xA9B40100u, 0xA9B40101u,
        };

        var pathA = NewTempPakPath();
        var pathB = NewTempPakPath();

        int rcA = BakeRunner.Run(new BakeOptions { DatDir = datDir, OutPath = pathA, IdFilter = ids, Threads = 8 });
        int rcB = BakeRunner.Run(new BakeOptions { DatDir = datDir, OutPath = pathB, IdFilter = ids, Threads = 3 });

        Assert.Equal(0, rcA);
        Assert.Equal(0, rcB);

        var bytesA = File.ReadAllBytes(pathA);
        var bytesB = File.ReadAllBytes(pathB);
        Assert.True(bytesA.Length == bytesB.Length,
            $"pak sizes differ between runs: {bytesA.Length} vs {bytesB.Length} bytes");
        Assert.True(bytesA.AsSpan().SequenceEqual(bytesB),
            "two bakes of the same id set produced different bytes — the sorted-batching determinism guarantee is broken");
        using var reader = new PakReader(pathA);
        foreach (uint gfxId in new[]
        {
            0x01000001u,
            0x010002B4u,
            0x010008A8u,
            0x010014C3u,
        })
        {
            Assert.True(reader.ContainsKey(PakKey.Compose(
                PakAssetType.GfxObjCollision,
                gfxId)));
        }
        foreach (uint setupId in new[]
        {
            0x02000001u,
            0x020019FFu,
            0x020005D8u,
        })
        {
            Assert.True(reader.ContainsKey(PakKey.Compose(
                PakAssetType.SetupCollision,
                setupId)));
        }
        foreach (uint cellId in new[]
        {
            0xA9B40100u,
            0xA9B40101u,
        })
        {
            Assert.True(reader.ContainsKey(PakKey.Compose(
                PakAssetType.CellStructureCollision,
                cellId)));
            Assert.True(reader.ContainsKey(PakKey.Compose(
                PakAssetType.EnvCellTopology,
                cellId)));
        }
    }

    [Fact]
    public void Bake_RealDuplicateCells_ShareOneBlobAcrossLandblocksAndThreads() {
        var datDir = ResolveDatDir();
        if (datDir is null) Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        var ids = new HashSet<uint> {
            0x00A90230u, 0x00B901A8u, 0x00E50602u, 0x00E50610u,
            0x00E5061Fu, 0x00F90784u, 0x00F9078Eu, 0x00FA073Eu,
        };
        var pathA = NewTempPakPath();
        var pathB = NewTempPakPath();

        var reportA = BakeRunner.RunDetailed(
            new BakeOptions {
                DatDir = datDir,
                OutPath = pathA,
                IdFilter = ids,
                Threads = 1,
            });
        var reportB = BakeRunner.RunDetailed(
            new BakeOptions {
                DatDir = datDir,
                OutPath = pathB,
                IdFilter = ids,
                Threads = 8,
            });

        Assert.Equal(8, reportA.EnvCellKeys);
        Assert.Equal(1, reportA.UniqueEnvCellGeometries);
        Assert.Equal(7, reportA.EnvCellAliases);
        Assert.Equal(8, reportA.CellStructureCollisionKeys);
        Assert.Equal(1, reportA.UniqueCellStructureCollisions);
        Assert.Equal(7, reportA.CellStructureCollisionAliases);
        Assert.Equal(8, reportA.EnvCellTopologyKeys);
        Assert.Equal(10 + reportA.TexturePayloadKeys, reportA.PhysicalBlobs);
        Assert.Equal(reportA.TotalKeys, reportB.TotalKeys);
        Assert.True(File.ReadAllBytes(pathA).AsSpan().SequenceEqual(File.ReadAllBytes(pathB)));

        using var reader = new PakReader(pathA);
        var receipts = ids
            .Select(id => reader.GetTocEntryForTest(
                PakKey.Compose(PakAssetType.EnvCellMesh, id)))
            .ToArray();
        Assert.All(receipts, receipt => Assert.Equal(receipts[0].Offset, receipt.Offset));
        Assert.All(receipts, receipt => Assert.Equal(receipts[0].Length, receipt.Length));
        Assert.All(receipts, receipt => Assert.Equal(receipts[0].Crc32, receipt.Crc32));

        var collisionReceipts = ids
            .Select(id => reader.GetTocEntryForTest(
                PakKey.Compose(PakAssetType.CellStructureCollision, id)))
            .ToArray();
        Assert.All(
            collisionReceipts,
            receipt => Assert.Equal(
                collisionReceipts[0].Offset,
                receipt.Offset));
        Assert.All(
            collisionReceipts,
            receipt => Assert.Equal(
                collisionReceipts[0].Length,
                receipt.Length));
        Assert.Equal(
            ids.Count,
            ids.Select(id => reader.GetTocEntryForTest(
                    PakKey.Compose(PakAssetType.EnvCellTopology, id)).Offset)
                .Distinct()
                .Count());

        Assert.True(
            reader.TryReadObjectMeshData(
                PakKey.Compose(PakAssetType.EnvCellMesh, ids.Min()),
                out var geometry));
        Assert.NotNull(geometry);
        Assert.Equal(0xE000_0000_0000_0000UL,
            geometry.ObjectId & 0xF000_0000_0000_0000UL);
    }

    [Fact]
    public void Bake_CancellationCannotReplaceLastGoodPackage()
    {
        string? datDir = ResolveDatDir();
        if (datDir is null)
            Assert.Fail("Lane=InstalledDat requires an installed retail DAT directory; see docs/release-gate.md.");

        string destination = NewTempPakPath();
        byte[] lastGood = [0x41, 0x43, 0x50, 0x4B, 1, 2, 3, 4];
        File.WriteAllBytes(destination, lastGood);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        Assert.Throws<OperationCanceledException>(
            () => BakeRunner.RunDetailed(
                new BakeOptions
                {
                    DatDir = datDir,
                    OutPath = destination,
                    Threads = 2,
                    CancellationToken = cancellation.Token,
                }));

        Assert.Equal(lastGood, File.ReadAllBytes(destination));
        Assert.Empty(
            Directory.EnumerateFiles(
                Path.GetDirectoryName(destination)!,
                $".{Path.GetFileName(destination)}.*.tmp"));
    }
}
