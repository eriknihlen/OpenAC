using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeVendorRangeQueryTests
{
    private const uint Player = 0x50000001u;
    private const uint Vendor = 0x50000010u;
    private const uint Landblock = 0x01010001u;

    [Fact]
    public void EnforceRange_PlayerWithinVendorUseRadius_LeavesSessionOpen()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f); // 2 m away, 3 m radius
        Open(runtime, Vendor);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_PlayerMovesBeyondVendorUseRadius_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        // Walk 20 m away — well beyond the vendor's own authored 3 m radius.
        SetPosition(playerRecord, Landblock, 122f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_PlayerStaysWithinRadiusAfterSmallMove_LeavesSessionOpen()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        SetPosition(playerRecord, Landblock, 101f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_NoVendorOpen_IsANoOpAndDoesNotThrow()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_VendorUseRadiusAbsent_UsesRawZeroWithNoFallback()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: null);
        Open(runtime, Vendor);

        // Exact same position: distance 0 <= radius 0 — still open.
        RuntimeVendorRangeQuery.EnforceRange(runtime);
        Assert.Equal(Vendor, runtime.InventoryOwner.Vendor.VendorId);

        // Any nonzero move at all — even 5 cm — is out of range at radius 0.
        SetPosition(playerRecord, Landblock, 100.05f, 100f);
        RuntimeVendorRangeQuery.EnforceRange(runtime);
        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_VendorEntityRetired_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        RuntimeEntityRecord vendorRecord =
            Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.EntityObjects.Entities.RemoveActive(vendorRecord));

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_TeleportQueued_ClosesTheSessionBeforeArrival()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.TransitOwner.TryQueueTeleportStart(1));
        Assert.True(runtime.TransitOwner.HasPendingTeleportStart);
        Assert.False(runtime.TransitOwner.IsTeleportActive);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_TeleportActive_ClosesTheSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 100f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Assert.True(runtime.TransitOwner.TryQueueTeleportStart(1));
        Assert.True(runtime.TransitOwner.ActivateQueuedTeleport());
        Assert.True(runtime.TransitOwner.IsTeleportActive);

        RuntimeVendorRangeQuery.EnforceRange(runtime);

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void EnforceRange_ThrowingChangedObserverDuringAutoClose_DoesNotPropagate()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        RuntimeEntityRecord playerRecord =
            Add(runtime, Player, Landblock, 100f, 100f);
        Add(runtime, Vendor, Landblock, 102f, 100f, useRadius: 3f);
        Open(runtime, Vendor);

        Action<VendorTransition> throwingObserver =
            _ => throw new InvalidOperationException("boom");
        runtime.InventoryOwner.Vendor.Changed += throwingObserver;

        SetPosition(playerRecord, Landblock, 122f, 100f);
        var exception = Record.Exception(
            () => RuntimeVendorRangeQuery.EnforceRange(runtime));

        Assert.Null(exception);
        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);

        runtime.InventoryOwner.Vendor.Changed -= throwingObserver;
    }

    private static void Open(GameRuntime runtime, uint vendorGuid) =>
        Assert.True(runtime.InventoryOwner.Vendor.Apply(
            vendorGuid,
            default,
            Array.Empty<VendorShopItem>()));

    private static void SetPosition(
        RuntimeEntityRecord record,
        uint landblock,
        float x,
        float y,
        float z = 5f)
    {
        record.Snapshot = record.Snapshot with
        {
            Position = new CreateObject.ServerPosition(
                landblock, x, y, z, 1f, 0f, 0f, 0f),
        };
    }

    private static GameRuntime Create()
    {
        var operations = new Operations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static RuntimeEntityRecord Add(
        GameRuntime runtime,
        uint guid,
        uint landblock,
        float x,
        float y,
        float? useRadius = null)
    {
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(Spawn(guid, landblock, x, y, useRadius))
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
        return record;
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        uint landblock,
        float x,
        float y,
        float? useRadius) =>
        new(
            guid,
            new CreateObject.ServerPosition(landblock, x, y, 5f, 1f, 0f, 0f, 0f),
            0x02000001u,
            [],
            [],
            [],
            null,
            null,
            guid.ToString("X8"),
            null,
            null,
            null,
            UseRadius: useRadius);

    private sealed class Operations :
        IRuntimeCombatAttackOperations,
        IRuntimeCombatTargetOperations,
        IRuntimeCombatModeOperations,
        IRuntimeSpellCastOperations
    {
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
