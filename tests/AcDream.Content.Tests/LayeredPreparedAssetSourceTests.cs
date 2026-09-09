using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content.Pak;
using AcDream.Core.Physics;

namespace AcDream.Content.Tests;

public sealed class LayeredPreparedAssetSourceTests : IDisposable
{
    private static readonly PreparedAssetCatalogIdentity Identity =
        new(10, 20, 30, 40, PakFormat.CurrentBakeToolVersion);

    private readonly List<string> _paths = [];

    [Fact]
    public void OverlayWinsAndMissingRenderAndCollisionKeysFallBackToBase()
    {
        const uint replaced = 0x0100_0001u;
        const uint baseOnly = 0x0100_0002u;
        string basePath = WritePak(
            (replaced, 1f),
            (baseOnly, 2f));
        string overlayPath = WritePak((replaced, 9f));

        using var source = Open(basePath, overlayPath);
        PreparedAssetReadResult overlaid =
            source.Read(PreparedAssetRequest.GfxObj(replaced));
        PreparedAssetReadResult fallback =
            source.Read(PreparedAssetRequest.GfxObj(baseOnly));

        Assert.Equal(9f, overlaid.Data!.Vertices[0].Position.X);
        Assert.Equal(2f, fallback.Data!.Vertices[0].Position.X);
        Assert.Equal(
            PreparedAssetPresence.Available,
            source.Probe(PakAssetType.GfxObjMesh, baseOnly));

        PreparedCollisionReadResult<FlatSetupCollision> overlayCollision =
            source.ReadSetupCollision(replaced);
        PreparedCollisionReadResult<FlatSetupCollision> baseCollision =
            source.ReadSetupCollision(baseOnly);
        Assert.Equal(9f, overlayCollision.Data!.Height);
        Assert.Equal(2f, baseCollision.Data!.Height);

        Assert.Equal(3, source.Stats.Reads);
        Assert.Equal(2, source.Stats.Loaded);
        Assert.Equal(1, source.Stats.Missing);
        Assert.Equal(3, source.CollisionStats.Reads);
        Assert.Equal(2, source.CollisionStats.Loaded);
        Assert.Equal(1, source.CollisionStats.Missing);
        Assert.Equal(
            new FileInfo(basePath).Length + new FileInfo(overlayPath).Length,
            source.MappedVirtualBytes);
    }

    [Fact]
    public void CorruptOverlayKeyIsAuthoritativeAndNeverFallsBack()
    {
        const uint fileId = 0x0100_0001u;
        string basePath = WriteRenderOnlyPak((fileId, 1f));
        string overlayPath = WriteRenderOnlyPak((fileId, 9f));
        FlipFirstBlobByte(overlayPath);

        using var source = Open(basePath, overlayPath);
        PreparedAssetReadResult result =
            source.Read(PreparedAssetRequest.GfxObj(fileId));

        Assert.Equal(PreparedAssetReadStatus.Corrupt, result.Status);
        Assert.Null(result.Data);
        Assert.Equal(1, source.Stats.Reads);
        Assert.Equal(1, source.Stats.Corrupt);
        Assert.Equal(0, source.Stats.Loaded);
        Assert.Equal(
            PreparedAssetPresence.Corrupt,
            source.Probe(PakAssetType.GfxObjMesh, fileId));
    }

    [Fact]
    public void DisposeReleasesBothMappedPackagesAndIsIdempotent()
    {
        string basePath = WriteRenderOnlyPak((1u, 1f));
        string overlayPath = WriteRenderOnlyPak((2u, 2f));
        var source = Open(basePath, overlayPath);

        source.Dispose();
        source.Dispose();

        using var baseExclusive = new FileStream(
            basePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        using var overlayExclusive = new FileStream(
            overlayPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        Assert.True(baseExclusive.CanWrite);
        Assert.True(overlayExclusive.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => _ = source.Stats);
    }

    private LayeredPreparedAssetSource Open(string basePath, string overlayPath) =>
        new(
            new PakPreparedAssetSource(basePath, Identity),
            new PakPreparedAssetSource(overlayPath, Identity));

    private string WritePak(params (uint FileId, float Marker)[] entries)
    {
        string path = NewPath();
        using var writer = new PakWriter(path, Header());
        foreach ((uint fileId, float marker) in entries)
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjMesh, fileId),
                Mesh(fileId, marker));
            writer.AddBlob(
                PakKey.Compose(PakAssetType.SetupCollision, fileId),
                FlatCollisionAssetSerializer.Serialize(Setup(marker)));
        }
        writer.Finish();
        return path;
    }

    private string WriteRenderOnlyPak(params (uint FileId, float Marker)[] entries)
    {
        string path = NewPath();
        using var writer = new PakWriter(path, Header());
        foreach ((uint fileId, float marker) in entries)
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjMesh, fileId),
                Mesh(fileId, marker));
        }
        writer.Finish();
        return path;
    }

    private string NewPath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-layered-prepared-{Guid.NewGuid():N}.pak");
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

    private static ObjectMeshData Mesh(uint objectId, float marker) =>
        new()
        {
            ObjectId = objectId,
            Vertices =
            [
                new(
                    new Vector3(marker, 2, 3),
                    Vector3.UnitZ,
                    new Vector2(0.25f, 0.75f)),
            ],
        };

    private static FlatSetupCollision Setup(float marker) =>
        new(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            ImmutableArray<FlatCollisionSphere>.Empty,
            marker,
            marker,
            marker,
            marker);

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
