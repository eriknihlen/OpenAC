using AcDream.App.World;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;

namespace AcDream.App.Tests.World;

public sealed class DormantLiveEntityStoreTests
{
    private const uint Guid = 0x7000_0042u;

    [Theory]
    [InlineData(10, 10, (int)DormantCreateDisposition.ExistingGeneration)]
    [InlineData(10, 11, (int)DormantCreateDisposition.NewGeneration)]
    [InlineData(11, 10, (int)DormantCreateDisposition.StaleGeneration)]
    [InlineData(0xFFFF, 0, (int)DormantCreateDisposition.NewGeneration)]
    [InlineData(0, 0xFFFF, (int)DormantCreateDisposition.StaleGeneration)]
    public void CreateClassification_UsesRetailWrapSafeInstanceOrdering(
        ushort retained,
        ushort incoming,
        int expected)
    {
        var store = new DormantLiveEntityStore();
        store.Retain(Spawn(retained, 0x0101_0001u));

        Assert.Equal((DormantCreateDisposition)expected, store.ClassifyCreate(
            Spawn(incoming, 0x0101_0001u)));
    }

    [Fact]
    public void LandblockSnapshot_IsCanonicalAndDoesNotConsumeRecords()
    {
        var store = new DormantLiveEntityStore();
        WorldSession.EntitySpawn first = Spawn(1, 0x0101_0001u);
        WorldSession.EntitySpawn second = Spawn(2, 0x0202_0123u) with
        {
            Guid = Guid + 1,
        };
        store.Retain(first);
        store.Retain(second);

        Assert.Equal(first, Assert.Single(store.SnapshotLandblock(0x0101_FFFFu)));
        Assert.Equal(first, Assert.Single(store.SnapshotLandblock(0x0101_0123u)));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ExactDelete_CannotRemoveReusedGuidGeneration()
    {
        var store = new DormantLiveEntityStore();
        store.Retain(Spawn(2, 0x0101_0001u));

        Assert.False(store.RemoveExact(
            new DeleteObject.Parsed(Guid, InstanceSequence: 1)));
        Assert.True(store.Contains(Guid, generation: 2));
        Assert.True(store.RemoveExact(
            new DeleteObject.Parsed(Guid, InstanceSequence: 2)));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Retain_CannotReplaceNewerGenerationWithStaleSnapshot()
    {
        var store = new DormantLiveEntityStore();
        WorldSession.EntitySpawn newer = Spawn(2, 0x0101_0001u) with
        {
            Name = "newer",
        };
        store.Retain(newer);

        store.Retain(Spawn(1, 0x0202_0001u) with
        {
            Name = "stale",
        });

        Assert.True(store.TryGetExact(Guid, generation: 2, out var retained));
        Assert.Equal(newer, retained);
    }

    [Fact]
    public void Clear_DrainsSessionLifetime()
    {
        var store = new DormantLiveEntityStore();
        store.Retain(Spawn(1, 0x0101_0001u));

        store.Clear();

        Assert.Equal(0, store.Count);
    }

    private static WorldSession.EntitySpawn Spawn(
        ushort generation,
        uint cell) =>
        new(
            Guid,
            new CreateObject.ServerPosition(
                cell,
                10f,
                20f,
                5f,
                1f,
                0f,
                0f,
                0f),
            SetupTableId: 0x0200_0001u,
            AnimPartChanges: [],
            TextureChanges: [],
            SubPalettes: [],
            BasePaletteId: null,
            ObjScale: null,
            Name: "fixture",
            ItemType: 1u,
            MotionState: null,
            MotionTableId: null,
            InstanceSequence: generation);
}
