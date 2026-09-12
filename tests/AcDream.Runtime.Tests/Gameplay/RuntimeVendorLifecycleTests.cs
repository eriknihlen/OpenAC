using System.Buffers.Binary;
using System.Text;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeVendorLifecycleTests
{
    private const uint Player = 0x50000001u;

    [Fact]
    public void ApproachVendorEvent_PopulatesVendorStateWithProfileAndItems()
    {
        using GameRuntime runtime = Create();
        VendorState vendor = runtime.InventoryOwner.Vendor;
        using IDisposable wiring = Wire(vendor);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40001000u,
            categories: 0x42u,
            minValue: 5u,
            maxValue: 500u,
            dealsMagic: true,
            buyPrice: 0.8f,
            sellPrice: 1.3f,
            currencyWcid: 0u,
            currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    ItemGuid: 0x50002000u,
                    StackSize: 1,
                    Name: "Iron Sword",
                    WeenieClassId: 42u,
                    RawIconId: 0x1234u,
                    ItemType: (uint)ItemType.Weapon,
                    Value: 250),
            ]));

        Assert.Equal(0x40001000u, vendor.VendorId);
        Assert.Equal(0x42u, vendor.Profile.MerchandiseItemTypes);
        Assert.Equal(5u, vendor.Profile.MerchandiseMinValue);
        Assert.Equal(500u, vendor.Profile.MerchandiseMaxValue);
        Assert.True(vendor.Profile.DealMagicalItems);
        Assert.Equal(0.8f, vendor.Profile.BuyPrice);
        Assert.Equal(1.3f, vendor.Profile.SellPrice);

        VendorShopItem item = Assert.Single(vendor.Items);
        Assert.Equal(0x50002000u, item.ItemGuid);
        Assert.Equal(1, item.StackSize);
        Assert.Equal("Iron Sword", item.Name);
        Assert.Equal(42u, item.WeenieClassId);
        Assert.Equal((uint)ItemType.Weapon, item.ItemType);
        Assert.Equal(0x1234u | CreateObject.IconTypePrefix, item.IconId);
        // Value is the price-relevant field: it feeds VendorPricing's
        // BuyPrice/SellPrice formula alongside the profile rates above.
        Assert.Equal(250, item.Value);
    }

    [Fact]
    public void SecondApproachVendorEventFromDifferentVendor_ReplacesTheSession()
    {
        using GameRuntime runtime = Create();
        VendorState vendor = runtime.InventoryOwner.Vendor;
        using IDisposable wiring = Wire(vendor);
        var kinds = new List<VendorStateTransitionKind>();
        vendor.Changed += t => kinds.Add(t.Kind);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40001000u,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    0x50002000u, 1, "First Vendor Item", 1u, 1u,
                    (uint)ItemType.Misc, 10),
            ]));

        Assert.Equal(0x40001000u, vendor.VendorId);
        Assert.Single(vendor.Items);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40002000u,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    0x50003000u, 1, "Second Vendor Item A", 2u, 2u,
                    (uint)ItemType.Misc, 20),
                new VendorItemFixture(
                    0x50003001u, 1, "Second Vendor Item B", 3u, 3u,
                    (uint)ItemType.Misc, 30),
            ]));

        // Replaced, not merged: the second vendor's own guid and item list
        // only, the first vendor's items are gone.
        Assert.Equal(0x40002000u, vendor.VendorId);
        Assert.Equal(2, vendor.Items.Count);
        Assert.DoesNotContain(
            vendor.Items,
            i => i.ItemGuid == 0x50002000u);
        Assert.Contains(
            vendor.Items,
            i => i.Name == "Second Vendor Item A");

        Assert.Equal(
            [VendorStateTransitionKind.Opened, VendorStateTransitionKind.Opened],
            kinds);
    }

    [Fact]
    public void MalformedApproachVendorEvent_IsDroppedWithoutStateChange()
    {
        using GameRuntime runtime = Create();
        VendorState vendor = runtime.InventoryOwner.Vendor;
        using IDisposable wiring = Wire(vendor);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40001000u,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    0x50002000u, 1, "Untouched Item", 1u, 1u,
                    (uint)ItemType.Misc, 10),
            ]));
        Assert.Equal(0x40001000u, vendor.VendorId);

        Dispatch(new byte[] { 1, 2 });

        Assert.Equal(0x40001000u, vendor.VendorId);
        Assert.Single(vendor.Items);
        Assert.Equal("Untouched Item", vendor.Items[0].Name);
    }

    [Fact]
    public void ResetGeneration_ClearsTheOpenVendorSession()
    {
        using GameRuntime runtime = Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        Assert.True(runtime.InventoryOwner.Vendor.Apply(
            0x40001000u, default, Array.Empty<VendorShopItem>()));
        Assert.Equal(0x40001000u, runtime.InventoryOwner.Vendor.VendorId);

        runtime.ResetGeneration(new RuntimeGenerationToken(1), new NoOpResetHost());

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
        Assert.Empty(runtime.InventoryOwner.Vendor.Items);
    }

    [Fact]
    public void DisposingInventoryOwner_ClearsTheOpenVendorSession()
    {
        using GameRuntime runtime = Create();
        Assert.True(runtime.InventoryOwner.Vendor.Apply(
            0x40001000u, default, Array.Empty<VendorShopItem>()));

        runtime.InventoryOwner.Dispose();

        Assert.Equal(0u, runtime.InventoryOwner.Vendor.VendorId);
    }

    [Fact]
    public void ApproachVendorEvent_MaterializesShopItemsIntoTheOwnedObjectTable()
    {
        using GameRuntime runtime = Create();
        using IDisposable wiring = GameEventWiring.WireAll(
            _dispatcher,
            runtime.InventoryOwner.Objects,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            vendor: runtime.InventoryOwner.Vendor);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40001000u,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    0x50002000u, 1, "Iron Sword", 42u, 0x1234u,
                    (uint)ItemType.Weapon, 250),
            ]));

        ClientObject? shopItem = runtime.InventoryOwner.Objects.Get(0x50002000u);
        Assert.NotNull(shopItem);
        Assert.Equal(0x40001000u, shopItem!.ContainerId);
        Assert.Equal("Iron Sword", shopItem.Name);
        Assert.Equal(1, runtime.InventoryOwner.VendorItems.OwnedCount);

        runtime.InventoryOwner.Vendor.Close();

        Assert.Null(runtime.InventoryOwner.Objects.Get(0x50002000u));
        Assert.Equal(0, runtime.InventoryOwner.VendorItems.OwnedCount);
        RuntimeInventoryOwnershipSnapshot snapshot = runtime.InventoryOwner.CaptureOwnership();
        Assert.Equal(0u, snapshot.VendorId);
        Assert.Equal(0, snapshot.MaterializedVendorItemCount);
    }

    [Fact]
    public void ApproachVendorEvent_CarriesTheCompleteItemDescriptionThroughToTheObjectTable()
    {
        // A listed item used to lose the description fields the assessment
        // panel gates its weapon/coverage/capacity blocks on, so a vendor
        // listing read shorter than the same item in the pack.
        using GameRuntime runtime = Create();
        using IDisposable wiring = GameEventWiring.WireAll(
            _dispatcher,
            runtime.InventoryOwner.Objects,
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            vendor: runtime.InventoryOwner.Vendor);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: 0x40001000u,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items:
            [
                new VendorItemFixture(
                    0x50002000u, 1, "Silifi", 42u, 0x1234u,
                    (uint)ItemType.MeleeWeapon, 250,
                    ValidLocations: (uint)EquipMask.MeleeWeapon,
                    Priority: 0x00000002u,
                    ItemsCapacity: 7,
                    ContainersCapacity: 2,
                    Structure: 40,
                    MaxStructure: 100,
                    Workmanship: 8.5f,
                    Burden: 450,
                    MaterialType: 60u,
                    TargetType: 0x00000080u,
                    CombatUse: (byte)1,
                    AmmoType: (ushort)3,
                    ObjectDescriptionFlags: (uint)PublicWeenieFlags.Healer),
            ]));

        VendorShopItem listed = Assert.Single(runtime.InventoryOwner.Vendor.Items);
        Assert.Equal((uint)EquipMask.MeleeWeapon, listed.ValidLocations);
        Assert.Equal(0x00000002u, listed.Priority);
        Assert.Equal(7, listed.ItemsCapacity);
        Assert.Equal(2, listed.ContainersCapacity);
        Assert.Equal(40, listed.Structure);
        Assert.Equal(100, listed.MaxStructure);
        Assert.Equal(8.5f, listed.Workmanship);
        Assert.Equal(450, listed.Burden);
        Assert.Equal(60u, listed.MaterialType);
        Assert.Equal(0x00000080u, listed.TargetType);
        Assert.Equal((byte)1, listed.CombatUse);
        Assert.Equal((ushort)3, listed.AmmoType);
        Assert.Equal((uint)PublicWeenieFlags.Healer, listed.PublicWeenieBitfield);

        ClientObject? shopItem = runtime.InventoryOwner.Objects.Get(0x50002000u);
        Assert.NotNull(shopItem);
        Assert.Equal(EquipMask.MeleeWeapon, shopItem!.ValidLocations);
        Assert.Equal(0x00000002u, shopItem.Priority);
        Assert.Equal(7, shopItem.ItemsCapacity);
        Assert.Equal(2, shopItem.ContainersCapacity);
        Assert.Equal(40, shopItem.Structure);
        Assert.Equal(100, shopItem.MaxStructure);
        Assert.Equal(8.5f, shopItem.Workmanship);
        Assert.Equal(450, shopItem.Burden);
        Assert.Equal(60u, shopItem.MaterialType);
        Assert.Equal(0x00000080u, shopItem.TargetType);
        Assert.Equal((byte)1, shopItem.CombatUse);
        Assert.Equal((ushort)3, shopItem.AmmoType);
        Assert.Equal((uint)PublicWeenieFlags.Healer, shopItem.PublicWeenieBitfield);

        // The vendor still owns the listing: container and wield state are the
        // materializer's, not the description's.
        Assert.Equal(0x40001000u, shopItem.ContainerId);
        Assert.Equal(0u, shopItem.WielderId);
        Assert.Equal(EquipMask.None, shopItem.CurrentlyEquippedLocation);
    }

    [Fact]
    public void SecondUse_AfterLocalXClose_StillDispatchesOverTheWireAndReopensOnReapproach()
    {
        using GameRuntime runtime = Create();
        VendorState vendor = runtime.InventoryOwner.Vendor;
        using IDisposable wiring = Wire(vendor);
        RuntimeInteractionTransactionState transactions = runtime.ActionOwner.Transactions;
        var transport = new FakeTransport();
        const uint vendorGuid = 0x40001000u;

        ItemUseRequestReservation reservation1 =
            transactions.BeginUseRequestReservation();
        RuntimeInteractionDispatchResult result1 = transactions.TryDispatchUse(
            vendorGuid,
            ownedByPlayer: false,
            useable: true,
            reservation1,
            transport,
            out _);
        Assert.Equal(RuntimeInteractionDispatchResult.Dispatched, result1);
        Assert.Equal(new[] { vendorGuid }, transport.Uses);
        Assert.Equal(1, transactions.Inventory.BusyCount);

        Dispatch(BuildApproachVendorPayload(
            vendorGuid: vendorGuid,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items: []));
        Assert.Equal(vendorGuid, vendor.VendorId);

        transactions.CompleteUse(0u);
        Assert.Equal(0, transactions.Inventory.BusyCount);

        Assert.True(vendor.Close());
        Assert.Equal(0u, vendor.VendorId);

        ItemUseRequestReservation reservation2 =
            transactions.BeginUseRequestReservation();
        RuntimeInteractionDispatchResult result2 = transactions.TryDispatchUse(
            vendorGuid,
            ownedByPlayer: false,
            useable: true,
            reservation2,
            transport,
            out _);

        Assert.Equal(RuntimeInteractionDispatchResult.Dispatched, result2);
        Assert.Equal(new[] { vendorGuid, vendorGuid }, transport.Uses);
        Assert.Equal(1, transactions.Inventory.BusyCount);

        var kinds = new List<VendorStateTransitionKind>();
        vendor.Changed += t => kinds.Add(t.Kind);
        Dispatch(BuildApproachVendorPayload(
            vendorGuid: vendorGuid,
            categories: 0u, minValue: 0u, maxValue: 0u, dealsMagic: false,
            buyPrice: 1f, sellPrice: 1f, currencyWcid: 0u, currencyAmount: 0u,
            currencyName: "",
            items: []));

        Assert.Equal(vendorGuid, vendor.VendorId);
        Assert.Equal([VendorStateTransitionKind.Opened], kinds);

        transactions.CompleteUse(0u);
        Assert.Equal(0, transactions.Inventory.BusyCount);
    }

    private sealed class FakeTransport : IRuntimeInteractionTransport
    {
        private uint _sequence;
        public bool IsInWorld { get; set; } = true;
        public List<uint> Uses { get; } = [];

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            if (!IsInWorld)
            {
                sequence = 0u;
                return false;
            }
            sequence = ++_sequence;
            Uses.Add(serverGuid);
            return true;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = 0u;
            return false;
        }
    }

    [Fact]
    public void VendorId_IsTheLiveActiveVendorIdSeamSource()
    {
        using GameRuntime runtime = Create();
        VendorState vendor = runtime.InventoryOwner.Vendor;
        Assert.Equal(0u, vendor.VendorId);

        Assert.True(vendor.Apply(
            0x40001000u, default, Array.Empty<VendorShopItem>()));
        Assert.Equal(0x40001000u, vendor.VendorId);

        Assert.True(vendor.Close());
        Assert.Equal(0u, vendor.VendorId);
    }

    // ---- fixtures / helpers ------------------------------------------

    private readonly record struct VendorItemFixture(
        uint ItemGuid,
        int StackSize,
        string Name,
        uint WeenieClassId,
        uint RawIconId,
        uint ItemType,
        int? Value,
        uint? ValidLocations = null,
        uint? Priority = null,
        int? ItemsCapacity = null,
        int? ContainersCapacity = null,
        int? Structure = null,
        int? MaxStructure = null,
        float? Workmanship = null,
        int? Burden = null,
        uint? MaterialType = null,
        uint? TargetType = null,
        byte? CombatUse = null,
        ushort? AmmoType = null,
        uint ObjectDescriptionFlags = 0u);

    private IDisposable Wire(VendorState vendor) => GameEventWiring.WireAll(
        _dispatcher,
        new ClientObjectTable(),
        new CombatState(),
        new Spellbook(),
        new ChatLog(),
        vendor: vendor);

    private readonly GameEventDispatcher _dispatcher = new();

    private void Dispatch(byte[] payload)
    {
        GameEventEnvelope? envelope = GameEventEnvelope.TryParse(
            WrapEnvelope(GameEventType.ApproachVendor, payload));
        Assert.NotNull(envelope);
        _dispatcher.Dispatch(envelope.Value);
    }

    private static byte[] WrapEnvelope(GameEventType type, byte[] payload)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)type);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static byte[] BuildApproachVendorPayload(
        uint vendorGuid,
        uint categories,
        uint minValue,
        uint maxValue,
        bool dealsMagic,
        float buyPrice,
        float sellPrice,
        uint currencyWcid,
        uint currencyAmount,
        string currencyName,
        IReadOnlyList<VendorItemFixture> items)
    {
        var b = new List<byte>();
        WireU32(b, vendorGuid);
        WireU32(b, categories);
        WireU32(b, minValue);
        WireU32(b, maxValue);
        WireU32(b, dealsMagic ? 1u : 0u);
        WireF32(b, buyPrice);
        WireF32(b, sellPrice);
        WireU32(b, currencyWcid);
        WireU32(b, currencyAmount);
        WireStr16L(b, currencyName);
        WireU32(b, (uint)items.Count);
        foreach (VendorItemFixture item in items)
        {
            uint packed = ((uint)item.StackSize & 0xFFFFFFu) | 0xFF000000u;
            WireU32(b, packed);
            WireU32(b, item.ItemGuid);

            // Fixed PWD prefix, then the optional tail in the exact order the
            // description parser reads it.
            uint weenieFlags = 0u;
            if (item.ItemsCapacity.HasValue) weenieFlags |= 0x00000002u;
            if (item.ContainersCapacity.HasValue) weenieFlags |= 0x00000004u;
            if (item.AmmoType.HasValue) weenieFlags |= 0x00000100u;
            if (item.Value.HasValue) weenieFlags |= 0x00000008u;
            if (item.TargetType.HasValue) weenieFlags |= 0x00080000u;
            if (item.CombatUse.HasValue) weenieFlags |= 0x00000200u;
            if (item.Structure.HasValue) weenieFlags |= 0x00000400u;
            if (item.MaxStructure.HasValue) weenieFlags |= 0x00000800u;
            if (item.ValidLocations.HasValue) weenieFlags |= 0x00010000u;
            if (item.Priority.HasValue) weenieFlags |= 0x00040000u;
            if (item.Workmanship.HasValue) weenieFlags |= 0x01000000u;
            if (item.Burden.HasValue) weenieFlags |= 0x00200000u;
            if (item.MaterialType.HasValue) weenieFlags |= 0x80000000u;
            WireU32(b, weenieFlags);
            WireStr16L(b, item.Name);
            WirePackedDword(b, item.WeenieClassId);
            WirePackedDword(b, item.RawIconId);
            WireU32(b, item.ItemType);
            WireU32(b, item.ObjectDescriptionFlags);
            WireAlign(b);
            if (item.ItemsCapacity.HasValue)
                b.Add(unchecked((byte)(sbyte)item.ItemsCapacity.Value));
            if (item.ContainersCapacity.HasValue)
                b.Add(unchecked((byte)(sbyte)item.ContainersCapacity.Value));
            if (item.AmmoType.HasValue)
                WireU16(b, item.AmmoType.Value);
            if (item.Value.HasValue)
                WireU32(b, unchecked((uint)item.Value.Value));
            if (item.TargetType.HasValue)
                WireU32(b, item.TargetType.Value);
            if (item.CombatUse.HasValue)
                b.Add(item.CombatUse.Value);
            if (item.Structure.HasValue)
                WireU16(b, (ushort)item.Structure.Value);
            if (item.MaxStructure.HasValue)
                WireU16(b, (ushort)item.MaxStructure.Value);
            if (item.ValidLocations.HasValue)
                WireU32(b, item.ValidLocations.Value);
            if (item.Priority.HasValue)
                WireU32(b, item.Priority.Value);
            if (item.Workmanship.HasValue)
                WireF32(b, item.Workmanship.Value);
            if (item.Burden.HasValue)
                WireU16(b, (ushort)item.Burden.Value);
            if (item.MaterialType.HasValue)
                WireU32(b, item.MaterialType.Value);
            WireAlign(b);
        }
        return b.ToArray();
    }

    private static void WireU32(List<byte> b, uint v)
    {
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(t, v);
        b.AddRange(t.ToArray());
    }

    private static void WireF32(List<byte> b, float v) =>
        WireU32(b, unchecked((uint)BitConverter.SingleToInt32Bits(v)));

    private static void WireU16(List<byte> b, ushort v)
    {
        Span<byte> t = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(t, v);
        b.AddRange(t.ToArray());
    }

    private static void WireStr16L(List<byte> b, string s)
    {
        byte[] bytes = Encoding.GetEncoding(1252).GetBytes(s);
        WireU16(b, (ushort)bytes.Length);
        b.AddRange(bytes);
        int total = 2 + bytes.Length;
        int pad = (4 - (total & 3)) & 3;
        for (int i = 0; i < pad; i++) b.Add(0);
    }

    private static void WirePackedDword(List<byte> b, uint v)
    {
        if (v <= 32767)
        {
            WireU16(b, (ushort)v);
            return;
        }
        uint packed = (v << 16) | ((v >> 16) | 0x8000);
        WireU32(b, packed);
    }

    private static void WireAlign(List<byte> b)
    {
        int pad = (4 - (b.Count & 3)) & 3;
        for (int i = 0; i < pad; i++) b.Add(0);
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

    private sealed class NoOpResetHost : IRuntimeGenerationResetHost
    {
        public void RetireEntityProjection(RuntimeEntityRecord entity) { }
        public void DrainEntityProjectionBoundary() { }
        public void CompleteEntityProjectionRetirement() { }
    }

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
