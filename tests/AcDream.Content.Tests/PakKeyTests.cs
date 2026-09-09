using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public class PakKeyTests {
    [Theory]
    [InlineData(PakAssetType.GfxObjMesh, 0u)]
    [InlineData(PakAssetType.GfxObjMesh, 0x01000001u)]
    [InlineData(PakAssetType.SetupMesh, 0x020019FFu)]
    [InlineData(PakAssetType.EnvCellMesh, 0xA9B40100u)]
    [InlineData(PakAssetType.GfxObjMesh, 0xFFFFFFFFu)] // max fileId
    [InlineData(PakAssetType.EnvCellMesh, 0xFFFFFFFFu)]
    public void ComposeDecompose_RoundTrips(PakAssetType type, uint fileId) {
        ulong key = PakKey.Compose(type, fileId);
        var (decodedType, decodedFileId) = PakKey.Decompose(key);
        Assert.Equal(type, decodedType);
        Assert.Equal(fileId, decodedFileId);
    }

    [Fact]
    public void Compose_LowFourBytesReserved() {
        // Low 24 bits are reserved (zero in v1) — fileId << 24 must not spill
        // into them, and the reserved region must actually read back as zero.
        ulong key = PakKey.Compose(PakAssetType.GfxObjMesh, 0xFFFFFFFFu);
        Assert.Equal(0u, (uint)(key & 0xFFFFFFu));
    }

    [Fact]
    public void Compose_TypeOccupiesTopByte() {
        ulong key = PakKey.Compose(PakAssetType.SetupMesh, 0u);
        Assert.Equal((byte)PakAssetType.SetupMesh, (byte)(key >> 56));
    }

    [Theory]
    [InlineData(PakAssetType.GfxObjMesh, 1)]
    [InlineData(PakAssetType.SetupMesh, 2)]
    [InlineData(PakAssetType.EnvCellMesh, 3)]
    [InlineData(PakAssetType.GfxObjCollision, 4)]
    [InlineData(PakAssetType.SetupCollision, 5)]
    [InlineData(PakAssetType.CellStructureCollision, 6)]
    [InlineData(PakAssetType.EnvCellTopology, 7)]
    public void AssetType_NumericValuesAreStable(PakAssetType type, byte expected) {
        Assert.Equal(expected, (byte)type);
    }

    [Fact]
    public void KeyOrdering_MatchesTypeThenFileIdOrdering() {
        // Ascending key order must equal ascending (type, fileId) tuple order —
        // this is what makes the TOC's binary search over raw u64 keys valid.
        var pairs = new (PakAssetType Type, uint FileId)[] {
            (PakAssetType.GfxObjMesh, 0u),
            (PakAssetType.GfxObjMesh, 1u),
            (PakAssetType.GfxObjMesh, 0xFFFFFFFFu),
            (PakAssetType.SetupMesh, 0u),
            (PakAssetType.SetupMesh, 0x020019FFu),
            (PakAssetType.EnvCellMesh, 0u),
            (PakAssetType.EnvCellMesh, 0xA9B40100u),
        };

        var keys = pairs.Select(p => PakKey.Compose(p.Type, p.FileId)).ToArray();

        // keys[] as generated must already be strictly ascending since pairs[]
        // is in ascending tuple order.
        for (int i = 1; i < keys.Length; i++) {
            Assert.True(keys[i] > keys[i - 1],
                $"key[{i}]=0x{keys[i]:X16} should be > key[{i - 1}]=0x{keys[i - 1]:X16} " +
                $"for tuple order ({pairs[i - 1]}) < ({pairs[i]})");
        }

        // Sorting the keys numerically must reproduce the same order as sorting
        // the tuples lexicographically by (Type, FileId).
        var numericSorted = keys.OrderBy(k => k).ToArray();
        var tupleSorted = pairs.OrderBy(p => (byte)p.Type).ThenBy(p => p.FileId)
            .Select(p => PakKey.Compose(p.Type, p.FileId)).ToArray();
        Assert.Equal(tupleSorted, numericSorted);
    }
}
