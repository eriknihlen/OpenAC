using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeEntityPvpBitfieldSnapshotSyncTests
{
    private const uint Guid = 0x70000020u;

    private static WorldSession.EntitySpawn Spawn(uint? objectDescriptionFlags) => new(
        Guid,
        new CreateObject.ServerPosition(0x0101FFFFu, 10f, 10f, 5f, 1f, 0f, 0f, 0f),
        0x02000001u,
        Array.Empty<CreateObject.AnimPartChange>(),
        Array.Empty<CreateObject.TextureChange>(),
        Array.Empty<CreateObject.SubPaletteSwap>(),
        null,
        null,
        "fixture",
        (uint)ItemType.Creature,
        null,
        0x09000001u,
        ObjectDescriptionFlags: objectDescriptionFlags);

    [Fact]
    public void ObjectUpdated_WithLiveBitfield_RewritesSnapshotObjectDescriptionFlags()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        using var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        WorldSession.EntitySpawn spawn = Spawn(0x8u); // BF_PLAYER only
        entities.AcceptCreate(spawn);
        RuntimeEntityRecord record = entities.AddActive(spawn);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Guid,
            PublicWeenieBitfield = 0x8u,
        });

        objects.UpdateIntProperty(
            Guid,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        Assert.Equal(0x2000008u, record.Snapshot.ObjectDescriptionFlags);
    }

    [Fact]
    public void ObjectUpdated_UnrelatedProperty_DoesNotRewriteSnapshot()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        using var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        WorldSession.EntitySpawn spawn = Spawn(0x8u);
        entities.AcceptCreate(spawn);
        RuntimeEntityRecord record = entities.AddActive(spawn);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Guid,
            PublicWeenieBitfield = 0x8u,
        });

        // Effects (18) is unrelated to PK status; PublicWeenieBitfield is
        // untouched, so the idempotency guard must skip the rewrite.
        objects.UpdateIntProperty(Guid, ClientObjectTable.UiEffectsPropertyId, value: 4);

        Assert.Equal(0x8u, record.Snapshot.ObjectDescriptionFlags);
    }

    [Fact]
    public void ObjectUpdated_NoPublicWeenieBitfieldYet_NoOp()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        using var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        RuntimeEntityRecord record = entities.AddActive(Spawn(null));
        objects.AddOrUpdate(new ClientObject { ObjectId = Guid });

        objects.UpdateIntProperty(Guid, ClientObjectTable.UiEffectsPropertyId, value: 1);

        Assert.Null(record.Snapshot.ObjectDescriptionFlags);
    }

    [Fact]
    public void ObjectUpdated_NoMatchingActiveEntity_NoOp()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        using var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        // Item exists in the object table but has no active Runtime entity
        // (e.g. an inventory item) — must not throw.
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 0x80000001u,
            PublicWeenieBitfield = 0x8u,
        });

        objects.UpdateIntProperty(
            0x80000001u,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.Pk);

        Assert.Equal(0, entities.Count);
    }

    [Fact]
    public void ObjDescAfterPkUpdate_MergedSnapshotKeepsLiveBitfield()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        using var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        WorldSession.EntitySpawn spawn = Spawn(0x8u);
        entities.AcceptCreate(spawn);
        entities.AddActive(spawn);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Guid,
            PublicWeenieBitfield = 0x8u,
        });

        objects.UpdateIntProperty(
            Guid,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        var update = new ObjDescEvent.Parsed(
            Guid,
            new CreateObject.ModelData(
                0x04000001u,
                Array.Empty<CreateObject.SubPaletteSwap>(),
                Array.Empty<CreateObject.TextureChange>(),
                Array.Empty<CreateObject.AnimPartChange>()),
            InstanceSequence: 0,
            ObjDescSequence: 1);
        Assert.True(entities.TryApplyObjDesc(update, out WorldSession.EntitySpawn accepted));
        Assert.Equal(0x2000008u, accepted.ObjectDescriptionFlags);
    }

    [Fact]
    public void Dispose_UnsubscribesFromObjectUpdated()
    {
        var entities = new RuntimeEntityDirectory();
        var objects = new ClientObjectTable();
        var sync = new RuntimeEntityPvpBitfieldSnapshotSync(entities, objects);

        WorldSession.EntitySpawn spawn = Spawn(0x8u);
        entities.AcceptCreate(spawn);
        RuntimeEntityRecord record = entities.AddActive(spawn);
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Guid,
            PublicWeenieBitfield = 0x8u,
        });

        sync.Dispose();
        objects.UpdateIntProperty(
            Guid,
            ClientObjectTable.PlayerKillerStatusPropertyId,
            value: PlayerKillerStatusBitfield.PkLite);

        Assert.Equal(0x8u, record.Snapshot.ObjectDescriptionFlags);
    }
}
