using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public sealed class PakAliasTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"acdream-pak-alias-{Guid.NewGuid():N}.pak");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
        }
    }

    [Fact]
    public void Alias_HasIndependentKeyAndSharedPhysicalReceipt()
    {
        var primaryKey = PakKey.Compose(PakAssetType.EnvCellMesh, 0xA9B4_0100);
        var aliasKey = PakKey.Compose(PakAssetType.EnvCellMesh, 0xA9B4_0101);
        var expected = MakeData(0x2_1234_5678);

        using (var writer = NewWriter())
        {
            writer.AddBlob(primaryKey, expected);
            writer.AddAlias(aliasKey, primaryKey);
            writer.Finish();
        }

        using var reader = new PakReader(_path);
        Assert.True(reader.TryReadObjectMeshData(primaryKey, out var primary));
        Assert.True(reader.TryReadObjectMeshData(aliasKey, out var alias));
        Assert.NotNull(primary);
        Assert.NotNull(alias);
        Assert.Equal(expected.ObjectId, primary.ObjectId);
        Assert.Equal(expected.ObjectId, alias.ObjectId);

        var primaryReceipt = reader.GetTocEntryForTest(primaryKey);
        var aliasReceipt = reader.GetTocEntryForTest(aliasKey);
        Assert.Equal(primaryReceipt.Offset, aliasReceipt.Offset);
        Assert.Equal(primaryReceipt.Length, aliasReceipt.Length);
        Assert.Equal(primaryReceipt.Crc32, aliasReceipt.Crc32);
        Assert.Equal(2u, reader.Header.TocCount);
    }

    [Fact]
    public void Alias_CorruptSharedPayload_MakesBothKeysMissing()
    {
        var primaryKey = PakKey.Compose(PakAssetType.EnvCellMesh, 1);
        var aliasKey = PakKey.Compose(PakAssetType.EnvCellMesh, 2);

        using (var writer = NewWriter())
        {
            writer.AddBlob(primaryKey, MakeData(0x2_1234_5678));
            writer.AddAlias(aliasKey, primaryKey);
            writer.Finish();
        }

        long offset;
        using (var reader = new PakReader(_path))
            offset = reader.GetBlobOffsetForTest(primaryKey);

        using (var stream = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = offset;
            int value = stream.ReadByte();
            stream.Position = offset;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        using var corruptReader = new PakReader(_path);
        Assert.False(corruptReader.TryReadObjectMeshData(primaryKey, out _));
        Assert.False(corruptReader.TryReadObjectMeshData(aliasKey, out _));
    }

    [Fact]
    public void Alias_RejectsMissingSourceWithoutReservingAliasKey()
    {
        var sourceKey = PakKey.Compose(PakAssetType.EnvCellMesh, 1);
        var aliasKey = PakKey.Compose(PakAssetType.EnvCellMesh, 2);

        using var writer = NewWriter();
        Assert.Throws<KeyNotFoundException>(() => writer.AddAlias(aliasKey, sourceKey));

        writer.AddBlob(aliasKey, MakeData(2));
        writer.Finish();
    }

    [Fact]
    public void Alias_RejectsSelfAndDuplicateKeys()
    {
        var primaryKey = PakKey.Compose(PakAssetType.EnvCellMesh, 1);
        var aliasKey = PakKey.Compose(PakAssetType.EnvCellMesh, 2);

        using var writer = NewWriter();
        writer.AddBlob(primaryKey, MakeData(1));
        Assert.Throws<ArgumentException>(() => writer.AddAlias(primaryKey, primaryKey));
        writer.AddAlias(aliasKey, primaryKey);
        Assert.Throws<ArgumentException>(() => writer.AddAlias(aliasKey, primaryKey));
        Assert.Throws<ArgumentException>(() => writer.AddBlob(aliasKey, MakeData(2)));
        writer.Finish();
    }

    [Fact]
    public void Alias_RejectsMutationAfterFinish()
    {
        var primaryKey = PakKey.Compose(PakAssetType.EnvCellMesh, 1);
        var aliasKey = PakKey.Compose(PakAssetType.EnvCellMesh, 2);

        using var writer = NewWriter();
        writer.AddBlob(primaryKey, MakeData(1));
        writer.Finish();

        Assert.Throws<InvalidOperationException>(() => writer.AddAlias(aliasKey, primaryKey));
    }

    private PakWriter NewWriter() =>
        new(
            _path,
            new PakHeader
            {
                PortalIteration = 1,
                CellIteration = 2,
                HighResIteration = 3,
                LanguageIteration = 4,
                BakeToolVersion = 1,
            });

    private static ObjectMeshData MakeData(ulong objectId) =>
        new()
        {
            ObjectId = objectId,
            Vertices =
            [
                new(
                    new System.Numerics.Vector3(1, 2, 3),
                    System.Numerics.Vector3.UnitZ,
                    new System.Numerics.Vector2(0.25f, 0.75f)),
            ],
        };
}
