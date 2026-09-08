using System.Numerics;
using AcDream.Content.Pak;

namespace AcDream.Content.Tests;

public sealed class PreparedAssetSourceTests : IDisposable
{
    private static readonly PreparedAssetCatalogIdentity Identity =
        new(10, 20, 30, 40, PakFormat.CurrentBakeToolVersion);

    private readonly List<string> _paths = [];

    [Fact]
    public void Probe_IsTypedAndDoesNotFaultPayloadBytes()
    {
        string path = WritePak(
            (PakAssetType.GfxObjMesh, 0x0100_0001u, Mesh(0x0100_0001u)));

        using var source = new PakPreparedAssetSource(path, Identity);
        Assert.Equal(
            PreparedAssetPresence.Available,
            source.Probe(PakAssetType.GfxObjMesh, 0x0100_0001u));
        Assert.Equal(
            PreparedAssetPresence.Missing,
            source.Probe(PakAssetType.SetupMesh, 0x0100_0001u));
        Assert.Equal(2, source.Stats.Probes);
        Assert.Equal(0, source.Stats.Reads);
    }

    [Fact]
    public void Read_PreservesMissingLoadedAndCorruptStates()
    {
        const uint fileId = 0x0100_0001u;
        string path = WritePak(
            (PakAssetType.GfxObjMesh, fileId, Mesh(fileId)));

        using (var source = new PakPreparedAssetSource(path, Identity))
        {
            PreparedAssetReadResult loaded =
                source.Read(PreparedAssetRequest.GfxObj(fileId));
            Assert.Equal(PreparedAssetReadStatus.Loaded, loaded.Status);
            Assert.NotNull(loaded.Data);

            PreparedAssetReadResult missing =
                source.Read(PreparedAssetRequest.GfxObj(0x0100_FFFFu));
            Assert.Equal(PreparedAssetReadStatus.Missing, missing.Status);
            Assert.Null(missing.Data);
        }

        FlipFirstBlobByte(path);
        using var corruptSource = new PakPreparedAssetSource(path, Identity);
        Assert.Equal(
            PreparedAssetPresence.Available,
            corruptSource.Probe(PakAssetType.GfxObjMesh, fileId));
        PreparedAssetReadResult corrupt =
            corruptSource.Read(PreparedAssetRequest.GfxObj(fileId));
        Assert.Equal(PreparedAssetReadStatus.Corrupt, corrupt.Status);
        Assert.Null(corrupt.Data);
        Assert.Equal(
            PreparedAssetPresence.Corrupt,
            corruptSource.Probe(PakAssetType.GfxObjMesh, fileId));
    }

    [Fact]
    public void Read_EnvCellAliasUsesSourceKeyAndRuntimeGeometryIdentity()
    {
        const uint primaryCell = 0x1234_0101u;
        const uint aliasCell = 0x1234_0102u;
        const ulong geometryId = 0x2_0000_1234UL;
        string path = NewPath();
        using (var writer = new PakWriter(path, Header()))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.EnvCellMesh, primaryCell),
                Mesh(geometryId));
            writer.AddAlias(
                PakKey.Compose(PakAssetType.EnvCellMesh, aliasCell),
                PakKey.Compose(PakAssetType.EnvCellMesh, primaryCell));
            writer.Finish();
        }

        using var source = new PakPreparedAssetSource(path, Identity);
        var request = PreparedAssetRequest.EnvCellGeometry(
            aliasCell,
            geometryId,
            environmentId: 7,
            cellStructure: 3,
            surfaces: [1, 2, 3]);

        PreparedAssetReadResult result = source.Read(request);
        Assert.Equal(PreparedAssetReadStatus.Loaded, result.Status);
        Assert.Equal(geometryId, result.Data!.ObjectId);
    }

    [Fact]
    public void Read_RejectsPayloadIdentityOrTypeMismatchAsCorruption()
    {
        const uint setupId = 0x0200_0001u;
        string path = WritePak(
            (PakAssetType.SetupMesh, setupId, Mesh(setupId)));
        var diagnostics = new List<string>();

        using var source = new PakPreparedAssetSource(
            path,
            Identity,
            diagnostics.Add);
        PreparedAssetReadResult result =
            source.Read(PreparedAssetRequest.Setup(setupId));

        Assert.Equal(PreparedAssetReadStatus.Corrupt, result.Status);
        Assert.Single(diagnostics);
        Assert.Contains("identity mismatch", diagnostics[0]);
    }

    [Fact]
    public void Constructor_RejectsEveryCatalogIdentityMismatch()
    {
        string path = WritePak(
            (PakAssetType.GfxObjMesh, 1u, Mesh(1u)));
        var mismatches = new[]
        {
            Identity with { PortalIteration = 11 },
            Identity with { CellIteration = 21 },
            Identity with { HighResIteration = 31 },
            Identity with { LanguageIteration = 41 },
            Identity with { BakeToolVersion = Identity.BakeToolVersion + 1 },
        };

        foreach (PreparedAssetCatalogIdentity expected in mismatches)
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(
                () => new PakPreparedAssetSource(path, expected));
            Assert.Contains("does not match the installed DAT set", error.Message);
            Assert.Contains("Re-bake", error.Message);
        }
    }

    [Fact]
    public void Constructor_RejectsOlderRecipePackageBeforePayloadDecode()
    {
        string path = WritePak(
            (PakAssetType.EnvCellMesh, 1u, Mesh(1u)));

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Position = 36;
            Span<byte> buf = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                buf, 9u);
            fs.Write(buf);
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => new PakPreparedAssetSource(path, Identity));
        Assert.Contains("does not match the installed DAT set", error.Message);
        Assert.Contains("Re-bake", error.Message);
        Assert.Contains("bake tool 9 != 10", error.Message);
    }

    [Fact]
    public void Read_PreCanceledTokenPropagatesWithoutStartingRead()
    {
        const uint fileId = 0x0100_0001u;
        string path = WritePak(
            (PakAssetType.GfxObjMesh, fileId, Mesh(fileId)));
        using var source = new PakPreparedAssetSource(path, Identity);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => source.Read(
                PreparedAssetRequest.GfxObj(fileId),
                cancellation.Token));
        Assert.Equal(0, source.Stats.Reads);
    }

    [Fact]
    public void Dispose_ReleasesMappedPackage()
    {
        string path = WritePak(
            (PakAssetType.GfxObjMesh, 1u, Mesh(1u)));
        var source = new PakPreparedAssetSource(path, Identity);
        source.Dispose();

        using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    private string WritePak(
        params (PakAssetType Type, uint FileId, ObjectMeshData Data)[] entries)
    {
        string path = NewPath();
        using var writer = new PakWriter(path, Header());
        foreach ((PakAssetType type, uint fileId, ObjectMeshData data) in entries)
        {
            writer.AddBlob(PakKey.Compose(type, fileId), data);
        }
        writer.Finish();
        return path;
    }

    private string NewPath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-prepared-{Guid.NewGuid():N}.pak");
        _paths.Add(path);
        return path;
    }

    private static PakHeader Header() =>
        new()
        {
            PortalIteration = Identity.PortalIteration,
            CellIteration = Identity.CellIteration,
            HighResIteration = Identity.HighResIteration,
            LanguageIteration = Identity.LanguageIteration,
            BakeToolVersion = Identity.BakeToolVersion,
        };

    private static ObjectMeshData Mesh(ulong objectId) =>
        new()
        {
            ObjectId = objectId,
            Vertices =
            [
                new(
                    new Vector3(1, 2, 3),
                    Vector3.UnitZ,
                    new Vector2(0.25f, 0.75f)),
            ],
        };

    private static void FlipFirstBlobByte(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.Position = PakHeader.Size + 4;
        int value = stream.ReadByte();
        Assert.NotEqual(-1, value);
        stream.Position = PakHeader.Size + 4;
        stream.WriteByte((byte)(value ^ 0xFF));
    }

    public void Dispose()
    {
        foreach (string path in _paths)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
