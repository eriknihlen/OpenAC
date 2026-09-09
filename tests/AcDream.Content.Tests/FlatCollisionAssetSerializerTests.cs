using System.Collections.Immutable;
using System.Numerics;
using AcDream.Content.Pak;
using AcDream.Core.Physics;
using DatReaderWriter.Enums;

namespace AcDream.Content.Tests;

public sealed class FlatCollisionAssetSerializerTests : IDisposable
{
    private static readonly PreparedAssetCatalogIdentity Identity =
        new(10, 20, 30, 40, PakFormat.CurrentBakeToolVersion);

    private readonly List<string> _paths = [];

    [Fact]
    public void EveryPayload_RoundTripsToByteIdenticalRepresentation()
    {
        FlatGfxObjCollisionAsset gfx = GfxAsset();
        FlatSetupCollision setup = SetupAsset();
        FlatCellStructureCollisionAsset structure = CellStructureAsset();
        FlatEnvCellTopology topology = TopologyAsset();

        byte[] gfxBytes = FlatCollisionAssetSerializer.Serialize(gfx);
        byte[] setupBytes = FlatCollisionAssetSerializer.Serialize(setup);
        byte[] structureBytes =
            FlatCollisionAssetSerializer.Serialize(structure);
        byte[] topologyBytes =
            FlatCollisionAssetSerializer.Serialize(topology);

        FlatGfxObjCollisionAsset readGfx =
            FlatCollisionAssetSerializer.DeserializeGfxObj(gfxBytes);
        FlatSetupCollision readSetup =
            FlatCollisionAssetSerializer.DeserializeSetup(setupBytes);
        FlatCellStructureCollisionAsset readStructure =
            FlatCollisionAssetSerializer.DeserializeCellStructure(
                structureBytes);
        FlatEnvCellTopology readTopology =
            FlatCollisionAssetSerializer.DeserializeEnvCellTopology(
                topologyBytes);

        Assert.Equal(
            gfxBytes,
            FlatCollisionAssetSerializer.Serialize(readGfx));
        Assert.Equal(
            setupBytes,
            FlatCollisionAssetSerializer.Serialize(readSetup));
        Assert.Equal(
            structureBytes,
            FlatCollisionAssetSerializer.Serialize(readStructure));
        Assert.Equal(
            topologyBytes,
            FlatCollisionAssetSerializer.Serialize(readTopology));

        AssertFloatBits(
            gfx.VisualBounds!.Value.Radius,
            readGfx.VisualBounds!.Value.Radius);
        AssertFloatBits(
            setup.StepDownHeight,
            readSetup.StepDownHeight);
        AssertFloatBits(
            structure.PhysicsBsp.Nodes[0].SplittingPlane.D,
            readStructure.PhysicsBsp.Nodes[0].SplittingPlane.D);
        Assert.Equal(
            topology.VisibleCellIds.AsEnumerable(),
            readTopology.VisibleCellIds.AsEnumerable());
    }

    [Fact]
    public void Encoding_IsLittleEndianAndPreservesSpecialFloatBits()
    {
        float special = BitConverter.Int32BitsToSingle(
            unchecked((int)0xFFC0_1234u));
        var setup = new FlatSetupCollision(
            ImmutableArray<FlatCollisionCylinder>.Empty,
            ImmutableArray<FlatCollisionSphere>.Empty,
            special,
            -0f,
            float.PositiveInfinity,
            float.NegativeInfinity);

        byte[] bytes = FlatCollisionAssetSerializer.Serialize(setup);

        Assert.Equal(
            new byte[]
            {
                0x41, 0x43, 0x43, 0x4C,
                0x02, 0x01, 0x00, 0x00,
            },
            bytes[..8]);
        Assert.Equal(
            new byte[] { 0x34, 0x12, 0xC0, 0xFF },
            bytes[16..20]);

        FlatSetupCollision read =
            FlatCollisionAssetSerializer.DeserializeSetup(bytes);
        AssertFloatBits(special, read.Height);
        AssertFloatBits(-0f, read.Radius);
        AssertFloatBits(float.PositiveInfinity, read.StepUpHeight);
        AssertFloatBits(float.NegativeInfinity, read.StepDownHeight);
    }

    [Fact]
    public void Readers_RejectWrongKindTruncationTrailingAndInvalidCounts()
    {
        byte[] setup = FlatCollisionAssetSerializer.Serialize(SetupAsset());

        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetSerializer.DeserializeGfxObj(setup));

        for (int length = 0; length < setup.Length; length++)
        {
            Assert.ThrowsAny<Exception>(
                () => FlatCollisionAssetSerializer.DeserializeSetup(
                    setup.AsSpan(0, length)));
        }

        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetSerializer.DeserializeSetup(
                [.. setup, 0x00]));

        byte[] negativeCount = (byte[])setup.Clone();
        negativeCount[8] = 0xFF;
        negativeCount[9] = 0xFF;
        negativeCount[10] = 0xFF;
        negativeCount[11] = 0xFF;
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetSerializer.DeserializeSetup(
                negativeCount));

        byte[] impossibleCombinedCounts = (byte[])setup.Clone();
        impossibleCombinedCounts[8] = 0x03;
        impossibleCombinedCounts[12] = 0x03;
        Assert.Throws<InvalidDataException>(
            () => FlatCollisionAssetSerializer.DeserializeSetup(
                impossibleCombinedCounts));
    }

    [Fact]
    public void SerializeAndDeserialize_PropagatePreCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => FlatCollisionAssetSerializer.Serialize(
                GfxAsset(),
                cancellation.Token));
        byte[] bytes =
            FlatCollisionAssetSerializer.Serialize(GfxAsset());
        Assert.Throws<OperationCanceledException>(
            () => FlatCollisionAssetSerializer.DeserializeGfxObj(
                bytes,
                cancellation.Token));
    }

    [Fact]
    public void PreparedSource_ReadsAllCollisionKindsThroughSamePackageOwner()
    {
        const uint gfxId = 0x0100_0001u;
        const uint setupId = 0x0200_0001u;
        const uint cellId = 0xA9B4_0101u;
        string path = NewPath();
        using (var writer = new PakWriter(path, Header()))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.GfxObjCollision, gfxId),
                FlatCollisionAssetSerializer.Serialize(GfxAsset()));
            writer.AddBlob(
                PakKey.Compose(PakAssetType.SetupCollision, setupId),
                FlatCollisionAssetSerializer.Serialize(SetupAsset()));
            writer.AddBlob(
                PakKey.Compose(
                    PakAssetType.CellStructureCollision,
                    cellId),
                FlatCollisionAssetSerializer.Serialize(CellStructureAsset()));
            writer.AddBlob(
                PakKey.Compose(PakAssetType.EnvCellTopology, cellId),
                FlatCollisionAssetSerializer.Serialize(TopologyAsset()));
            writer.Finish();
        }

        using var owner = new PakPreparedAssetSource(path, Identity);
        IPreparedAssetSource renderSource = owner;
        IPreparedCollisionSource collisionSource = owner;

        Assert.Equal(
            PreparedAssetPresence.Available,
            collisionSource.ProbeCollision(
                PakAssetType.GfxObjCollision,
                gfxId));
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            collisionSource.ReadGfxObjCollision(gfxId).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            collisionSource.ReadSetupCollision(setupId).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            collisionSource.ReadCellStructureCollision(cellId).Status);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            collisionSource.ReadEnvCellTopology(cellId).Status);
        Assert.Equal(4, collisionSource.CollisionStats.Loaded);
        Assert.Equal(0, renderSource.Stats.Reads);
        Assert.Equal(new FileInfo(path).Length, renderSource.MappedVirtualBytes);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => renderSource.Probe(
                PakAssetType.GfxObjCollision,
                gfxId));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => collisionSource.ProbeCollision(
                PakAssetType.GfxObjMesh,
                gfxId));
    }

    [Fact]
    public void PreparedSource_DistinguishesMissingCrcAndSchemaCorruption()
    {
        const uint setupId = 0x0200_0001u;
        string missingPath = NewPath();
        using (var writer = new PakWriter(missingPath, Header()))
            writer.Finish();

        using (var source = new PakPreparedAssetSource(
                   missingPath,
                   Identity))
        {
            Assert.Equal(
                PreparedAssetReadStatus.Missing,
                source.ReadSetupCollision(setupId).Status);
        }

        string schemaPath = NewPath();
        byte[] malformed =
            [.. FlatCollisionAssetSerializer.Serialize(SetupAsset()), 0x7F];
        using (var writer = new PakWriter(schemaPath, Header()))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.SetupCollision, setupId),
                malformed);
            writer.Finish();
        }

        using (var source = new PakPreparedAssetSource(schemaPath, Identity))
        {
            Assert.Equal(
                PreparedAssetReadStatus.Corrupt,
                source.ReadSetupCollision(setupId).Status);
            Assert.Equal(
                PreparedAssetPresence.Corrupt,
                source.ProbeCollision(
                    PakAssetType.SetupCollision,
                    setupId));
            Assert.Equal(
                PreparedAssetReadStatus.Corrupt,
                source.ReadSetupCollision(setupId).Status);
        }

        string crcPath = NewPath();
        using (var writer = new PakWriter(crcPath, Header()))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.SetupCollision, setupId),
                FlatCollisionAssetSerializer.Serialize(SetupAsset()));
            writer.Finish();
        }
        FlipFirstBlobByte(crcPath);
        using var corruptSource =
            new PakPreparedAssetSource(crcPath, Identity);
        Assert.Equal(
            PreparedAssetReadStatus.Corrupt,
            corruptSource.ReadSetupCollision(setupId).Status);
    }

    [Fact]
    public void PreparedSource_ConcurrentReadsRemainIndependentAndComplete()
    {
        const uint setupId = 0x0200_0001u;
        string path = NewPath();
        using (var writer = new PakWriter(path, Header()))
        {
            writer.AddBlob(
                PakKey.Compose(PakAssetType.SetupCollision, setupId),
                FlatCollisionAssetSerializer.Serialize(SetupAsset()));
            writer.Finish();
        }

        using var source = new PakPreparedAssetSource(path, Identity);
        Parallel.For(
            0,
            64,
            _ =>
            {
                PreparedCollisionReadResult<FlatSetupCollision> result =
                    source.ReadSetupCollision(setupId);
                Assert.Equal(PreparedAssetReadStatus.Loaded, result.Status);
                Assert.NotNull(result.Data);
            });

        Assert.Equal(64, source.CollisionStats.Reads);
        Assert.Equal(64, source.CollisionStats.Loaded);
        Assert.Equal(0, source.CollisionStats.Corrupt);
    }

    [Fact]
    public void RenderDecoder_CannotPoisonCollisionTypedEntry()
    {
        const uint setupId = 0x0200_0001u;
        string path = NewPath();
        ulong key = PakKey.Compose(
            PakAssetType.SetupCollision,
            setupId);
        using (var writer = new PakWriter(path, Header()))
        {
            writer.AddBlob(
                key,
                FlatCollisionAssetSerializer.Serialize(SetupAsset()));
            writer.Finish();
        }

        using (var reader = new PakReader(path))
        {
            Assert.Equal(
                PakObjectReadStatus.Missing,
                reader.ReadObjectMeshData(key, out ObjectMeshData? mesh));
            Assert.Null(mesh);
            Assert.Equal(PakEntryState.Available, reader.ProbeEntry(key));
        }

        using var source = new PakPreparedAssetSource(path, Identity);
        Assert.Equal(
            PreparedAssetReadStatus.Loaded,
            source.ReadSetupCollision(setupId).Status);
    }

    private static FlatGfxObjCollisionAsset GfxAsset() =>
        new(
            PhysicsBsp(),
            new FlatCollisionSphere(
                new Vector3(Float(0x3F80_0001), -0f, 3f),
                Float(0x7FC0_1234)),
            new FlatGfxObjVisualBounds(
                new Vector3(-1, -2, -3),
                new Vector3(4, 5, 6),
                new Vector3(1.5f),
                Float(0x4123_4567),
                new Vector3(2.5f, 3.5f, 4.5f)));

    private static FlatSetupCollision SetupAsset() =>
        new(
            ImmutableArray.Create(
                new FlatCollisionCylinder(
                    new Vector3(1, 2, 3),
                    Float(0x3F00_0001),
                    Float(0x4000_0001))),
            ImmutableArray.Create(
                new FlatCollisionSphere(
                    new Vector3(-1, -2, -3),
                    Float(0x4040_0001))),
            Float(0x4080_0001),
            Float(0x40A0_0001),
            Float(0x40C0_0001),
            Float(0x40E0_0001));

    private static FlatCellStructureCollisionAsset CellStructureAsset() =>
        new(
            PhysicsBsp(),
            new FlatCellContainmentBsp(
                0,
                ImmutableArray.Create(
                    new FlatCellBspNode(
                        BSPNodeType.BPIN,
                        new Plane(Vector3.UnitX, -1),
                        1,
                        2,
                        0),
                    new FlatCellBspNode(
                        BSPNodeType.Leaf,
                        new Plane(Vector3.UnitY, -2),
                        -1,
                        -1,
                        1),
                    new FlatCellBspNode(
                        BSPNodeType.Leaf,
                        new Plane(Vector3.UnitZ, -3),
                        -1,
                        -1,
                        2))),
            PolygonTable());

    private static FlatEnvCellTopology TopologyAsset() =>
        new(
            ImmutableArray.Create(
                new FlatEnvCellPortal(0x0102, 3, 4, 0),
                new FlatEnvCellPortal(0x0103, 8, 2, 1)),
            ImmutableArray.Create(0xA9B4_0101u, 0xA9B4_0102u),
            true);

    private static FlatPhysicsBsp PhysicsBsp() =>
        new(
            0,
            ImmutableArray.Create(
                Node(BSPNodeType.BPIN, 1, 2, 0, 0, 0),
                Node(BSPNodeType.Leaf, -1, -1, 1, 0, 1),
                Node(BSPNodeType.Leaf, -1, -1, 2, 1, 1)),
            ImmutableArray.Create(0, 1),
            PolygonTable());

    private static FlatPhysicsBspNode Node(
        BSPNodeType type,
        int positive,
        int negative,
        int leaf,
        int polygonStart,
        int polygonCount) =>
        new(
            type,
            new Plane(
                new Vector3(1 + leaf, 2 + leaf, 3 + leaf),
                Float(0x3F80_0000u + (uint)leaf)),
            positive,
            negative,
            leaf,
            leaf & 1,
            new FlatCollisionSphere(
                new Vector3(4 + leaf, 5 + leaf, 6 + leaf),
                7 + leaf),
            new FlatIndexRange(polygonStart, polygonCount));

    private static FlatPolygonTable PolygonTable() =>
        new(
            ImmutableArray.Create(
                new FlatCollisionPolygon(
                    3,
                    new Plane(Vector3.UnitX, -1),
                    CullMode.Clockwise,
                    3,
                    new FlatIndexRange(0, 3)),
                new FlatCollisionPolygon(
                    8,
                    new Plane(Vector3.UnitY, -2),
                    CullMode.CounterClockwise,
                    3,
                    new FlatIndexRange(3, 3))),
            ImmutableArray.Create(
                Vector3.Zero,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.One,
                Vector3.UnitX,
                Vector3.UnitZ));

    private string NewPath()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-flat-collision-{Guid.NewGuid():N}.pak");
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

    private static float Float(uint bits) =>
        BitConverter.Int32BitsToSingle(unchecked((int)bits));

    private static void AssertFloatBits(float expected, float actual) =>
        Assert.Equal(
            BitConverter.SingleToInt32Bits(expected),
            BitConverter.SingleToInt32Bits(actual));

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
