using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Runtime.Entities;

namespace AcDream.Runtime.Tests.Entities;

public sealed class RuntimeEntityDirectoryTests
{
    [Fact]
    public void AddActive_OwnsOneCurrentIncarnationPerGuid()
    {
        var directory = new RuntimeEntityDirectory();
        RuntimeEntityRecord first = directory.AddActive(Spawn(0x70000001u, 1));

        Assert.True(directory.TryGetActive(first.ServerGuid, out RuntimeEntityRecord found));
        Assert.Same(first, found);
        Assert.True(directory.IsCurrent(first));
        Assert.Throws<InvalidOperationException>(
            () => directory.AddActive(Spawn(first.ServerGuid, 2)));
    }

    [Fact]
    public void RemoveAndRetainTombstone_AllowsReplacementWithoutLosingExactOldIncarnation()
    {
        var directory = new RuntimeEntityDirectory();
        RuntimeEntityRecord old = directory.AddActive(Spawn(0x70000002u, 7));

        Assert.True(directory.RemoveActive(old.ServerGuid, out RuntimeEntityRecord? removed));
        Assert.Same(old, removed);
        directory.RetainTeardown(old);
        RuntimeEntityRecord replacement = directory.AddActive(Spawn(old.ServerGuid, 8));

        Assert.True(directory.TryGetActive(old.ServerGuid, out RuntimeEntityRecord current));
        Assert.Same(replacement, current);
        Assert.True(directory.TryGetTeardown(old.ServerGuid, 7, out RuntimeEntityRecord tombstone));
        Assert.Same(old, tombstone);
        Assert.False(directory.IsCurrent(old));
        Assert.True(directory.IsCurrent(replacement));
    }

    [Fact]
    public void LocalIdentity_IsOwnedByDirectoryAndReverseLookupRejectsReleasedIncarnation()
    {
        var directory = new RuntimeEntityDirectory();
        RuntimeEntityRecord old = directory.AddActive(Spawn(0x70000003u, 1));
        uint oldLocalId = directory.ClaimLocalId(old);
        Assert.True(directory.TryGetByLocalId(oldLocalId, out RuntimeEntityRecord foundOld));
        Assert.Same(old, foundOld);

        Assert.True(directory.RemoveActive(old.ServerGuid, out _));
        directory.RetainTeardown(old);
        RuntimeEntityRecord replacement = directory.AddActive(Spawn(old.ServerGuid, 2));
        uint replacementLocalId = directory.ClaimLocalId(replacement);

        Assert.NotEqual(oldLocalId, replacementLocalId);
        Assert.True(directory.ReleaseLocalId(old));
        Assert.False(directory.TryGetByLocalId(oldLocalId, out _));
        Assert.True(directory.TryGetByLocalId(
            replacementLocalId,
            out RuntimeEntityRecord foundReplacement));
        Assert.Same(replacement, foundReplacement);
    }

    [Fact]
    public void LocalIdentity_WrapsWithinReservedNamespaceWithoutCollision()
    {
        var directory = new RuntimeEntityDirectory(RuntimeEntityDirectory.LastLocalEntityId);
        RuntimeEntityRecord first = directory.AddActive(Spawn(0x70000004u, 1));
        RuntimeEntityRecord second = directory.AddActive(Spawn(0x70000005u, 1));

        Assert.Equal(
            RuntimeEntityDirectory.LastLocalEntityId,
            directory.ClaimLocalId(first));
        Assert.Equal(
            RuntimeEntityDirectory.FirstLocalEntityId,
            directory.ClaimLocalId(second));
    }

    [Fact]
    public void SessionClear_AdvancesEpochAndPreservesRetryableTombstone()
    {
        var directory = new RuntimeEntityDirectory();
        RuntimeEntityRecord record = directory.AddActive(Spawn(0x70000006u, 3));
        ulong operation = directory.AdvanceLifetimeMutation(record.ServerGuid);
        Assert.Equal(operation, directory.CurrentLifetimeMutation(record.ServerGuid));

        Assert.True(directory.RemoveActive(record.ServerGuid, out _));
        directory.RetainTeardown(record);
        directory.BeginSessionClear();

        Assert.Equal(1UL, directory.SessionLifetimeVersion);
        Assert.Equal(0UL, directory.CurrentLifetimeMutation(record.ServerGuid));
        Assert.False(directory.CompleteSessionClearIfConverged());
        Assert.True(directory.TryGetTeardown(record.ServerGuid, 3, out _));

        directory.ReleaseTeardown(record);
        Assert.True(directory.CompleteSessionClearIfConverged());
    }

    [Fact]
    public void AcceptedCreateSnapshotAndAuthorityLiveOnCanonicalRecord()
    {
        var directory = new RuntimeEntityDirectory();
        WorldSession.EntitySpawn spawn = Spawn(0x70000007u, 4);
        InboundCreateResult accepted = directory.AcceptCreate(spawn);
        RuntimeEntityRecord record = directory.AddActive(accepted.Snapshot);

        ulong priorCreateVersion = record.CreateIntegrationVersion;
        directory.AdvanceCreateAuthority(record);
        directory.RefreshSnapshot(
            record,
            accepted.Snapshot with { Name = "refreshed" },
            refreshPosition: true);

        Assert.Equal("refreshed", record.Snapshot.Name);
        Assert.Equal(priorCreateVersion + 1UL, record.CreateIntegrationVersion);
        Assert.Equal(spawn.Position!.Value.LandblockId, record.FullCellId);
    }

    [Fact]
    public void DeleteGateAndIdentityRemovalRemainSeparateRetryableEdges()
    {
        var directory = new RuntimeEntityDirectory();
        WorldSession.EntitySpawn spawn = Spawn(0x70000008u, 9);
        directory.AcceptCreate(spawn);
        RuntimeEntityRecord record = directory.AddActive(spawn);

        Assert.True(directory.TryDelete(
            new DeleteObject.Parsed(spawn.Guid, spawn.InstanceSequence),
            isLocalPlayer: false));
        Assert.True(directory.IsCurrent(record));
        Assert.True(directory.RemoveActive(record));
        directory.RetainTeardown(record);
        Assert.True(directory.TryGetTeardown(
            spawn.Guid,
            spawn.InstanceSequence,
            out RuntimeEntityRecord retained));
        Assert.Same(record, retained);
    }

    [Fact]
    public void CanonicalRecord_PublicSurfaceContainsNoAppOrBackendTypes()
    {
        Type canonical = typeof(RuntimeEntityRecord);
        Type[] exposed = canonical
            .GetProperties()
            .Select(property => property.PropertyType)
            .Concat(canonical.GetMethods().Select(method => method.ReturnType))
            .ToArray();

        Assert.DoesNotContain(exposed, type =>
            type.Namespace?.StartsWith("AcDream.App", StringComparison.Ordinal) == true
            || type.Namespace?.StartsWith("Silk.NET", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Constructor_RejectsIdsOutsideLiveNamespace()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RuntimeEntityDirectory(
                RuntimeEntityDirectory.FirstLocalEntityId - 1u));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new RuntimeEntityDirectory(
                RuntimeEntityDirectory.LastLocalEntityId + 1u));
    }

    private static WorldSession.EntitySpawn Spawn(uint guid, ushort instance)
    {
        var position = new CreateObject.ServerPosition(
            0x0101FFFFu,
            10f,
            20f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: 0x408u,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: 0x408u,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }
}
