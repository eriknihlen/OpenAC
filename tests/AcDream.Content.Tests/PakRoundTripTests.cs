using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public class PakRoundTripTests : IDisposable {
    private readonly List<string> _tempFiles = new();

    private string NewTempPakPath() {
        var path = Path.Combine(Path.GetTempPath(), $"acdream-paktest-{Guid.NewGuid():N}.pak");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose() {
        foreach (var f in _tempFiles) {
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    private static ObjectMeshData MakeSyntheticData(uint fileId, int vertexCount) {
        var vertices = new VertexPositionNormalTexture[vertexCount];
        for (int i = 0; i < vertexCount; i++) {
            vertices[i] = new VertexPositionNormalTexture(
                new Vector3(i, i * 2, i * 3),
                new Vector3(0, 0, 1),
                new Vector2(i * 0.1f, i * 0.2f));
        }
        return new ObjectMeshData {
            ObjectId = fileId,
            IsSetup = false,
            Vertices = vertices,
            DIDDegrade = fileId + 1,
        };
    }

    private static (PakAssetType Type, uint FileId, ObjectMeshData Data)[] MakeBlobSet(int n) {
        var result = new (PakAssetType, uint, ObjectMeshData)[n];
        for (int i = 0; i < n; i++) {
            var type = (PakAssetType)((i % 3) + 1);
            uint fileId = 0x0100_0000u + (uint)i;
            result[i] = (type, fileId, MakeSyntheticData(fileId, i + 1));
        }
        return result;
    }

    private static PakHeader WritePak(string path, (PakAssetType Type, uint FileId, ObjectMeshData Data)[] blobs) {
        var header = new PakHeader {
            FormatVersion = PakFormat.CurrentFormatVersion,
            PortalIteration = 10,
            CellIteration = 20,
            HighResIteration = 30,
            LanguageIteration = 40,
            BakeToolVersion = PakFormat.CurrentBakeToolVersion,
        };
        using var writer = new PakWriter(path, header);
        foreach (var (type, fileId, data) in blobs) {
            writer.AddBlob(PakKey.Compose(type, fileId), data);
        }
        writer.Finish();
        return header;
    }

    [Fact]
    public void WriteThenOpen_HeaderFieldsMatch() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(5);
        var written = WritePak(path, blobs);

        using var reader = new PakReader(path);
        Assert.Equal(written.FormatVersion, reader.Header.FormatVersion);
        Assert.Equal(written.PortalIteration, reader.Header.PortalIteration);
        Assert.Equal(written.CellIteration, reader.Header.CellIteration);
        Assert.Equal(written.HighResIteration, reader.Header.HighResIteration);
        Assert.Equal(written.LanguageIteration, reader.Header.LanguageIteration);
        Assert.Equal(written.BakeToolVersion, reader.Header.BakeToolVersion);
        Assert.Equal((uint)blobs.Length, reader.Header.TocCount);
    }

    [Fact]
    public void WriteThenOpen_EveryKeyFound() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(20);
        WritePak(path, blobs);

        using var reader = new PakReader(path);
        foreach (var (type, fileId, _) in blobs) {
            Assert.True(reader.ContainsKey(PakKey.Compose(type, fileId)),
                $"key for type={type} fileId=0x{fileId:X8} should be found");
        }
    }

    [Fact]
    public void WriteThenOpen_BlobsDeserializeToDeepEqualObjects() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(8);
        WritePak(path, blobs);

        using var reader = new PakReader(path);
        foreach (var (type, fileId, expected) in blobs) {
            var key = PakKey.Compose(type, fileId);
            Assert.True(reader.TryReadObjectMeshData(key, out var actual), $"expected key 0x{key:X16} to read successfully");
            ObjectMeshDataEquality.AssertEqual(expected, actual);
        }
    }

    [Fact]
    public void MissingKey_ReturnsFalse() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(3);
        WritePak(path, blobs);

        using var reader = new PakReader(path);
        var missingKey = PakKey.Compose(PakAssetType.GfxObjMesh, 0xFFFF_FFFEu);
        Assert.False(reader.ContainsKey(missingKey));
        Assert.False(reader.TryReadObjectMeshData(missingKey, out var data));
        Assert.Null(data);
    }

    [Fact]
    public void CorruptedBlob_CrcMismatch_TreatedAsMissing() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(4);
        WritePak(path, blobs);

        // Flip one byte inside the FIRST blob's region (right after the 64-byte header).
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Position = PakHeader.Size + 4; // a few bytes into the first blob's payload
            int b = fs.ReadByte();
            fs.Position = PakHeader.Size + 4;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        using var reader = new PakReader(path);
        var key = PakKey.Compose(blobs[0].Type, blobs[0].FileId);
        Assert.False(reader.TryReadObjectMeshData(key, out var data),
            "a CRC-mismatched blob must be treated as missing");
        Assert.Null(data);
    }

    [Fact]
    public void CorruptedBlob_DoesNotAffectOtherBlobs() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(4);
        WritePak(path, blobs);

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Position = PakHeader.Size + 4;
            int b = fs.ReadByte();
            fs.Position = PakHeader.Size + 4;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        using var reader = new PakReader(path);
        // blobs[1..] should still read fine.
        for (int i = 1; i < blobs.Length; i++) {
            var key = PakKey.Compose(blobs[i].Type, blobs[i].FileId);
            Assert.True(reader.TryReadObjectMeshData(key, out var actual), $"blob {i} should be unaffected by corruption in blob 0");
            ObjectMeshDataEquality.AssertEqual(blobs[i].Data, actual);
        }
    }

    [Fact]
    public void Blobs_Are64ByteAligned() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(6);
        WritePak(path, blobs);

        using var reader = new PakReader(path);
        foreach (var (type, fileId, _) in blobs) {
            var offset = reader.GetBlobOffsetForTest(PakKey.Compose(type, fileId));
            Assert.True(offset % 64 == 0, $"blob offset {offset} for type={type} fileId=0x{fileId:X8} must be 64-byte aligned");
        }
    }

    [Fact]
    public void TocBinarySearch_MatchesLinearScan() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(50);
        WritePak(path, blobs);

        using var reader = new PakReader(path);
        var allKeys = blobs.Select(b => PakKey.Compose(b.Type, b.FileId)).ToArray();
        foreach (var key in allKeys) {
            bool binarySearchFound = reader.ContainsKey(key);
            bool linearScanFound = reader.DebugLinearScanContainsKey(key);
            Assert.Equal(linearScanFound, binarySearchFound);
        }

        // Also verify a handful of keys NOT present agree between both paths.
        var absentKeys = new[] {
            PakKey.Compose(PakAssetType.GfxObjMesh, 0xDEAD_0000u),
            PakKey.Compose(PakAssetType.SetupMesh, 0xDEAD_0001u),
            PakKey.Compose(PakAssetType.EnvCellMesh, 0xDEAD_0002u),
        };
        foreach (var key in absentKeys) {
            Assert.Equal(reader.DebugLinearScanContainsKey(key), reader.ContainsKey(key));
            Assert.False(reader.ContainsKey(key));
        }
    }

    [Fact]
    public void EmptyPak_OpensCleanly_NoKeysFound() {
        var path = NewTempPakPath();
        WritePak(path, Array.Empty<(PakAssetType, uint, ObjectMeshData)>());

        using var reader = new PakReader(path);
        Assert.Equal(0u, reader.Header.TocCount);
        Assert.False(reader.ContainsKey(PakKey.Compose(PakAssetType.GfxObjMesh, 1)));
    }

    // ---- format-version enforcement (review finding 2) ---------------------

    [Fact]
    public void Writer_StampsFormatVersion_IgnoringTemplate() {
        var path = NewTempPakPath();
        using (var writer = new PakWriter(path, new PakHeader { BakeToolVersion = 1 })) {
            writer.AddBlob(PakKey.Compose(PakAssetType.GfxObjMesh, 1), MakeSyntheticData(1, 1));
            writer.Finish();
        }

        using var reader = new PakReader(path);
        Assert.Equal(PakFormat.CurrentFormatVersion, reader.Header.FormatVersion);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(3u)]
    public void Reader_RejectsWrongFormatVersion(uint wrongVersion) {
        var path = NewTempPakPath();
        WritePak(path, MakeBlobSet(2));

        // Patch the header's formatVersion dword (offset 4) in place.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Position = 4;
            Span<byte> buf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, wrongVersion);
            fs.Write(buf);
        }

        var ex = Assert.Throws<InvalidDataException>(() => new PakReader(path));
        Assert.Contains($"version {wrongVersion}", ex.Message);
        Assert.Contains($"version {PakFormat.CurrentFormatVersion}", ex.Message);
    }

    // ---- reader robustness (review finding 3) -------------------------------

    [Fact]
    public void CorruptTocEntry_TreatedAsMissing_SiblingsUnaffected_NoThrow() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(4);
        WritePak(path, blobs);

        // Patch the FIRST victim entry's length field (entry offset +16) to a
        // value that runs past the file end.
        var victimKey = PakKey.Compose(blobs[0].Type, blobs[0].FileId);
        long entryPos = FindTocEntryPosition(path, victimKey);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Position = entryPos + 16;
            Span<byte> buf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf, 0x7FFF_FFF0u);
            fs.Write(buf);
        }

        using var reader = new PakReader(path);
        Assert.False(reader.ContainsKey(victimKey));
        Assert.False(reader.TryReadObjectMeshData(victimKey, out var data));
        Assert.Null(data);

        for (int i = 1; i < blobs.Length; i++) {
            var key = PakKey.Compose(blobs[i].Type, blobs[i].FileId);
            Assert.True(reader.TryReadObjectMeshData(key, out var sibling), $"sibling blob {i} should be unaffected");
            ObjectMeshDataEquality.AssertEqual(blobs[i].Data, sibling);
        }
    }

    [Fact]
    public void TruncatedPak_RefusedAtOpen() {
        var path = NewTempPakPath();
        WritePak(path, MakeBlobSet(4));

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.SetLength(fs.Length - PakTocEntry.Size - 4);
        }

        var ex = Assert.Throws<InvalidDataException>(() => new PakReader(path));
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void HalfWrittenPak_UnfinalizedHeader_RefusedAtOpen() {
        var path = NewTempPakPath();
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write)) {
            new PakHeader { FormatVersion = PakFormat.CurrentFormatVersion }.WriteTo(fs);
            var junk = new byte[256];
            new Random(42).NextBytes(junk);
            fs.Write(junk);
        }

        var ex = Assert.Throws<InvalidDataException>(() => new PakReader(path));
        Assert.Contains("unfinalized", ex.Message);
    }

    [Fact]
    public void MalformedBlobBehindValidCrc_TreatedAsMissing_SiblingsUnaffected() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(3);
        WritePak(path, blobs);

        var victimKey = PakKey.Compose(blobs[0].Type, blobs[0].FileId);
        long entryPos = FindTocEntryPosition(path, victimKey);
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            // Read the victim entry to get offset+length.
            fs.Position = entryPos;
            var entryBuf = new byte[PakTocEntry.Size];
            fs.ReadExactly(entryBuf);
            var entry = PakTocEntry.ReadFrom((ReadOnlySpan<byte>)entryBuf);

            fs.Position = (long)entry.Offset + 9;
            Span<byte> negOne = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(negOne, -1);
            fs.Write(negOne);

            // Recompute CRC over the tampered blob region.
            fs.Position = (long)entry.Offset;
            var blobBytes = new byte[entry.StoredLength];
            fs.ReadExactly(blobBytes);
            uint newCrc = Crc32.Compute(blobBytes);

            fs.Position = entryPos + 20;
            Span<byte> crcBuf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crcBuf, newCrc);
            fs.Write(crcBuf);
        }

        using var reader = new PakReader(path);
        Assert.False(reader.TryReadObjectMeshData(victimKey, out var data),
            "a structurally-malformed blob behind a matching CRC must be treated as missing");
        Assert.Null(data);
        Assert.False(reader.TryReadObjectMeshData(victimKey, out _));

        for (int i = 1; i < blobs.Length; i++) {
            var key = PakKey.Compose(blobs[i].Type, blobs[i].FileId);
            Assert.True(reader.TryReadObjectMeshData(key, out var sibling), $"sibling blob {i} should be unaffected");
            ObjectMeshDataEquality.AssertEqual(blobs[i].Data, sibling);
        }
    }

    // ---- writer disposal safety (review finding 5) ---------------------------

    [Fact]
    public void WriterDisposedAfterAddBlobException_ReleasesFileHandle() {
        var path = NewTempPakPath();
        var data = MakeSyntheticData(1, 1);
        var key = PakKey.Compose(PakAssetType.GfxObjMesh, 1);

        var writer = new PakWriter(path, new PakHeader { BakeToolVersion = 1 });
        writer.AddBlob(key, data);
        Assert.Throws<ArgumentException>(() => writer.AddBlob(key, data)); // duplicate key mid-AddBlob
        writer.Dispose();

        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    // ---- on-disk TOC sortedness (review finding 8) ---------------------------

    [Fact]
    public void OnDiskToc_IsSortedAscendingByKey_RegardlessOfAddOrder() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(12).OrderByDescending(b => PakKey.Compose(b.Type, b.FileId)).ToArray();
        WritePak(path, blobs);

        var fileBytes = File.ReadAllBytes(path);
        var header = PakHeader.ReadFrom((ReadOnlySpan<byte>)fileBytes);
        Assert.Equal((uint)blobs.Length, header.TocCount);

        ulong previousKey = 0;
        for (uint i = 0; i < header.TocCount; i++) {
            int pos = checked((int)((long)header.TocOffset + i * PakTocEntry.Size));
            var entry = PakTocEntry.ReadFrom(fileBytes.AsSpan(pos, PakTocEntry.Size));
            Assert.True(entry.Key > previousKey || i == 0,
                $"TOC entry {i} key 0x{entry.Key:X16} is not strictly greater than its predecessor 0x{previousKey:X16}");
            previousKey = entry.Key;
        }
    }


    [Fact]
    public void CorruptBlob_RepeatedReads_LogExactlyOnce() {
        var path = NewTempPakPath();
        var blobs = MakeBlobSet(2);
        WritePak(path, blobs);

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite)) {
            fs.Position = PakHeader.Size + 4;
            int b = fs.ReadByte();
            fs.Position = PakHeader.Size + 4;
            fs.WriteByte((byte)(b ^ 0xFF));
        }

        var victimKey = PakKey.Compose(blobs[0].Type, blobs[0].FileId);
        var originalError = Console.Error;
        var capture = new StringWriter();
        try {
            Console.SetError(capture);
            using var reader = new PakReader(path);
            Assert.False(reader.TryReadObjectMeshData(victimKey, out _));
            Assert.False(reader.TryReadObjectMeshData(victimKey, out _));
            Assert.False(reader.ContainsKey(victimKey));
            Assert.False(reader.ContainsKey(victimKey));
            Assert.False(reader.TryReadObjectMeshData(victimKey, out _));
        }
        finally {
            Console.SetError(originalError);
        }

        string logged = capture.ToString();
        int occurrences = CountOccurrences(logged, $"0x{victimKey:X16}");
        Assert.True(occurrences == 1,
            $"expected exactly ONE [pak-corrupt] line for key 0x{victimKey:X16} across 5 reads, got {occurrences}:\n{logged}");
    }

    private static int CountOccurrences(string haystack, string needle) {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }
        return count;
    }

    // ---- helpers -------------------------------------------------------------

    /// <summary>Locates the on-disk file position of the TOC entry for <paramref name="key"/> by raw parsing.</summary>
    private static long FindTocEntryPosition(string path, ulong key) {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        var headerBuf = new byte[PakHeader.Size];
        fs.ReadExactly(headerBuf);
        var header = PakHeader.ReadFrom((ReadOnlySpan<byte>)headerBuf);

        var entryBuf = new byte[PakTocEntry.Size];
        for (uint i = 0; i < header.TocCount; i++) {
            long pos = (long)header.TocOffset + i * PakTocEntry.Size;
            fs.Position = pos;
            fs.ReadExactly(entryBuf);
            var entry = PakTocEntry.ReadFrom((ReadOnlySpan<byte>)entryBuf);
            if (entry.Key == key) return pos;
        }
        throw new InvalidOperationException($"TOC entry for key 0x{key:X16} not found");
    }
}
