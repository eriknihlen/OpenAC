using System.Buffers.Binary;
using System.Security.Cryptography;
using AcDream.Content.Pak;
using Chorizite.Core.Render.Enums;
using DatReaderWriter.Enums;
using RetailCullMode = DatReaderWriter.Enums.CullMode;

namespace AcDream.Content.Tests;

public sealed class PakV2Tests : IDisposable
{
    private readonly List<string> _paths = [];

    [Fact]
    public void BlobCodec_UsesRawFallbackAndCompressedRoundTripsExactly()
    {
        byte[] small = [1, 2, 3, 4];
        PakBlobCodec.Encoded raw = PakBlobCodec.Encode(small);
        Assert.False(raw.Compressed);
        Assert.Equal(small, raw.Bytes);

        byte[] repetitive = new byte[64 * 1024];
        for (int i = 0; i < repetitive.Length; i++)
            repetitive[i] = (byte)(i & 7);

        PakBlobCodec.Encoded compressed = PakBlobCodec.Encode(repetitive);
        Assert.True(compressed.Compressed);
        Assert.True(compressed.Bytes.Length < repetitive.Length / 4);
        Assert.Equal(repetitive, PakBlobCodec.Decode(compressed.Bytes));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 4, 0, 0, 0, 255 })]
    public void BlobCodec_RejectsMalformedCompressedPayload(byte[] stored) =>
        Assert.Throws<InvalidDataException>(() => PakBlobCodec.Decode(stored));

    [Fact]
    public void BlobCodec_RejectsAllocationBombBeforeAllocating()
    {
        byte[] stored = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(
            stored,
            PakBlobCodec.MaximumDecodedBytes + 1u);

        Assert.Throws<InvalidDataException>(() => PakBlobCodec.Decode(stored));
    }

    [Fact]
    public void Writer_DeduplicatesTextureBytesGloballyAndReaderSharesArray()
    {
        string path = NewPath();
        byte[] texture = new byte[16 * 1024];
        for (int i = 0; i < texture.Length; i++)
            texture[i] = (byte)(i & 15);

        ObjectMeshData first = TexturedMesh(1, 0x0800_0001u, texture);
        ObjectMeshData second = TexturedMesh(2, 0x0800_0002u, texture.ToArray());
        using (var writer = NewWriter(path))
        {
            writer.AddBlob(PakKey.Compose(PakAssetType.GfxObjMesh, 1), first);
            writer.AddBlob(PakKey.Compose(PakAssetType.GfxObjMesh, 2), second);
            Assert.Equal(1, writer.TextureBlobCount);
            Assert.Equal(3, writer.EntryCount);
            writer.Finish();
        }

        using var reader = new PakReader(path);
        Assert.Equal(1, reader.CountEntries(PakAssetType.TexturePayload));
        Assert.True(reader.TryReadObjectMeshData(
            PakKey.Compose(PakAssetType.GfxObjMesh, 1),
            out ObjectMeshData? firstRead));
        Assert.True(reader.TryReadObjectMeshData(
            PakKey.Compose(PakAssetType.GfxObjMesh, 2),
            out ObjectMeshData? secondRead));
        ObjectMeshDataEquality.AssertEqual(first, firstRead);
        ObjectMeshDataEquality.AssertEqual(second, secondRead);
        Assert.Same(
            firstRead!.TextureBatches.Single().Value.Single().TextureData,
            secondRead!.TextureBatches.Single().Value.Single().TextureData);
    }

    [Fact]
    public void Writer_IsByteDeterministicWithExternalTexturesAndCompression()
    {
        string firstPath = NewPath();
        string secondPath = NewPath();
        byte[] texture = Enumerable.Repeat((byte)0x5A, 32 * 1024).ToArray();

        WriteDeterministic(firstPath, texture);
        WriteDeterministic(secondPath, texture);

        Assert.Equal(File.ReadAllBytes(firstPath), File.ReadAllBytes(secondPath));
    }

    [Fact]
    public void CorruptTextureDemotesOnlyReferencingMesh()
    {
        string path = NewPath();
        byte[] badTexture = Enumerable.Repeat((byte)0x11, 16 * 1024).ToArray();
        byte[] goodTexture = Enumerable.Repeat((byte)0x22, 16 * 1024).ToArray();
        ulong badMeshKey = PakKey.Compose(PakAssetType.GfxObjMesh, 1);
        ulong goodMeshKey = PakKey.Compose(PakAssetType.GfxObjMesh, 2);
        ulong badTextureKey = TexturePayloadKey(badTexture);
        using (var writer = NewWriter(path))
        {
            writer.AddBlob(badMeshKey, TexturedMesh(1, 1, badTexture));
            writer.AddBlob(goodMeshKey, TexturedMesh(2, 2, goodTexture));
            writer.Finish();
        }

        PakTocEntry textureEntry;
        using (var reader = new PakReader(path))
            textureEntry = reader.GetTocEntryForTest(badTextureKey);

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = (long)textureEntry.Offset + textureEntry.StoredLength / 2;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xFF));
        }

        using var corruptReader = new PakReader(path);
        Assert.Equal(
            PakObjectReadStatus.Corrupt,
            corruptReader.ReadObjectMeshData(badMeshKey, out _));
        Assert.Equal(PakEntryState.Corrupt, corruptReader.ProbeEntry(badTextureKey));
        Assert.Equal(
            PakObjectReadStatus.Loaded,
            corruptReader.ReadObjectMeshData(goodMeshKey, out ObjectMeshData? good));
        Assert.Equal(goodTexture, good!.TextureBatches.Single().Value.Single().TextureData);
    }

    [Fact]
    public void Reader_DemotesInvalidCompressedStreamBehindValidCrc()
    {
        string path = NewPath();
        ulong key = PakKey.Compose(PakAssetType.GfxObjCollision, 7);
        using (var writer = NewWriter(path))
        {
            writer.AddBlob(key, new byte[32 * 1024]);
            writer.Finish();
        }

        PakTocEntry entry;
        long tocEntryPosition;
        using (var reader = new PakReader(path))
        {
            entry = reader.GetTocEntryForTest(key);
            Assert.True(entry.IsCompressed);
            tocEntryPosition = FindTocEntryPosition(path, key);
        }

        byte[] invalid = new byte[checked((int)entry.StoredLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(invalid, 32 * 1024);
        invalid.AsSpan(sizeof(uint)).Fill(0xFF);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = (long)entry.Offset;
            stream.Write(invalid);
            stream.Position = tocEntryPosition + 20;
            Span<byte> crc = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(crc, Crc32.Compute(invalid));
            stream.Write(crc);
        }

        using var corruptReader = new PakReader(path);
        Assert.Equal(
            PakObjectReadStatus.Corrupt,
            corruptReader.ReadBlobBytes(key, out byte[]? bytes));
        Assert.Null(bytes);
        Assert.Equal(PakEntryState.Corrupt, corruptReader.ProbeEntry(key));
    }

    [Fact]
    public void TextureCache_IsReferenceSharingAndStrictlyBounded()
    {
        var cache = new PakTexturePayloadCache(maximumBytes: 6, maximumEntries: 2);
        byte[] first = [1, 2, 3, 4];
        byte[] second = [5, 6, 7, 8];

        Assert.Same(first, cache.AddOrGet(1, first));
        Assert.Same(first, cache.AddOrGet(1, first.ToArray()));
        Assert.Same(second, cache.AddOrGet(2, second));
        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out byte[] cachedSecond));
        Assert.Same(second, cachedSecond);
        Assert.Equal(1, cache.Count);
        Assert.Equal(4, cache.Bytes);

        byte[] tooLarge = new byte[7];
        Assert.Same(tooLarge, cache.AddOrGet(3, tooLarge));
        Assert.False(cache.TryGet(3, out _));
        Assert.Equal(4, cache.Bytes);
    }

    private static ObjectMeshData TexturedMesh(
        uint objectId,
        uint surfaceId,
        byte[] texture)
    {
        var mesh = new ObjectMeshData { ObjectId = objectId };
        mesh.TextureBatches[(64, 64, TextureFormat.RGBA8)] =
        [
            new TextureBatchData
            {
                Key = new TextureKey
                {
                    SurfaceId = surfaceId,
                    PaletteId = 1,
                    Stippling = StipplingType.Positive,
                },
                TextureData = texture,
                Indices = [0, 1, 2],
                CullMode = RetailCullMode.Clockwise,
            },
        ];
        return mesh;
    }

    private static PakWriter NewWriter(string path) =>
        new(path, new PakHeader
        {
            PortalIteration = 1,
            CellIteration = 2,
            HighResIteration = 3,
            LanguageIteration = 4,
        });

    private static void WriteDeterministic(string path, byte[] texture)
    {
        using var writer = NewWriter(path);
        writer.AddBlob(
            PakKey.Compose(PakAssetType.GfxObjMesh, 2),
            TexturedMesh(2, 12, texture.ToArray()));
        writer.AddBlob(
            PakKey.Compose(PakAssetType.GfxObjMesh, 1),
            TexturedMesh(1, 11, texture.ToArray()));
        writer.Finish();
    }

    private static ulong TexturePayloadKey(byte[] bytes)
    {
        byte[] digest = SHA256.HashData(bytes);
        ulong payloadId =
            ((ulong)digest[0] << 48)
            | ((ulong)digest[1] << 40)
            | ((ulong)digest[2] << 32)
            | ((ulong)digest[3] << 24)
            | ((ulong)digest[4] << 16)
            | ((ulong)digest[5] << 8)
            | digest[6];
        return PakKey.ComposeOpaque(PakAssetType.TexturePayload, payloadId);
    }

    private string NewPath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-pak-v2-{Guid.NewGuid():N}.pak");
        _paths.Add(path);
        return path;
    }

    private static long FindTocEntryPosition(string path, ulong key)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        PakHeader header = PakHeader.ReadFrom(stream);
        var bytes = new byte[PakTocEntry.Size];
        for (uint i = 0; i < header.TocCount; i++)
        {
            long position = checked((long)header.TocOffset + i * PakTocEntry.Size);
            stream.Position = position;
            stream.ReadExactly(bytes);
            if (PakTocEntry.ReadFrom(bytes).Key == key)
                return position;
        }

        throw new KeyNotFoundException($"pak key 0x{key:X16} not found");
    }

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            try { File.Delete(path); }
            catch (IOException) { }
        }
    }
}
