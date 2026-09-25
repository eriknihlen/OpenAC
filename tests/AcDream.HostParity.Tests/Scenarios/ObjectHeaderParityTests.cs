using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Physics;
using AcDream.Plugin.Abstractions;

namespace AcDream.HostParity.Tests;

/// <summary>
/// The raw description words and optional values of an object, as the
/// server sent them, reach a plugin on both clients -- for a thing out in
/// the world and for one in a pack -- and a value the description did not
/// carry reads as absent rather than zero.
///
/// Mutation checks (2026-09-25): dropping the header from the object-table
/// ingest turned both scenarios red; reading a packed item's use radius only
/// from a live body turned <see cref="EitherClientReportsAPackedItemsHeaderAndUseRadius"/>
/// red.
/// </summary>
public sealed class ObjectHeaderParityTests
{
    private const uint Lantern = 0x50000091u;
    private const uint Gem = 0x50000092u;

    [Fact]
    public void EitherClientReportsAWorldObjectsHeader() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            WorldSession.EntitySpawn spawn = ParityWorld.Spawn(
                Lantern,
                ParityWorld.PlayerX + 3f,
                ParityWorld.PlayerY,
                ParityPlayerBody.Cell,
                state: PhysicsStateFlags.Ethereal) with
            {
                WeenieHeaderFlags = 0x00280010u,
                WeenieHeaderFlags2 = null,
                PhysicsDescriptionFlags = 0x00008083u,
                ObjectDescriptionFlags = 0x00000010u,
                ObjScale = 1.25f,
                HookType = 2u,
                UseRadius = 3f,
            };
            arm.Server.CreateObject(spawn);
            arm.Advance();

            Assert.True(arm.Host.Automation.Objects.TryGet(Lantern, out PluginWorldObject lantern));
            PluginObjectHeader header = Assert.IsType<PluginObjectHeader>(lantern.Header);
            transcript.Record("weenieFlags", header.WeenieHeaderFlags);
            transcript.Record("physicsFlags", header.PhysicsDescriptionFlags);
            Assert.Equal(0x00280010u, header.WeenieHeaderFlags);
            Assert.Null(header.WeenieHeaderFlags2);
            Assert.Equal(0x00008083u, header.PhysicsDescriptionFlags);
            Assert.Equal((uint)PhysicsStateFlags.Ethereal, header.PhysicsState);
            Assert.Equal(0x00000010u, header.ObjectDescriptionFlags);
            Assert.Equal(0x02000001u, header.SetupId);
            Assert.Equal(1.25f, header.Scale);
            Assert.Equal(2u, header.HookType);
            Assert.Null(header.ParentObjectId);
            Assert.Null(header.ParentLocation);
            Assert.Equal(3f, header.UseRadius);
        });

    [Fact]
    public void EitherClientReportsAPackedItemsHeaderAndUseRadius() =>
        ParityScenario.Run(static (arm, transcript) =>
        {
            _ = ParityWorld.Stage(arm);
            WorldSession.EntitySpawn spawn = ParityWorld.Spawn(
                Gem,
                ParityWorld.PlayerX,
                ParityWorld.PlayerY,
                ParityPlayerBody.Cell,
                state: 0) with
            {
                Position = null,
                Physics = null,
                ContainerId = ParityWorld.Player,
                ItemType = 0x00000800u,
                WeenieHeaderFlags = 0x00200000u,
                WeenieHeaderFlags2 = 0x00000001u,
                PhysicsDescriptionFlags = 0u,
                ObjScale = null,
                UseRadius = 0.5f,
            };
            arm.Server.CreateObject(spawn);
            arm.Advance();

            PluginInventoryItem gem = Assert.Single(
                arm.Host.Automation.Items.CaptureOwnedItems(),
                static item => item.ObjectId == Gem);
            PluginObjectHeader header = Assert.IsType<PluginObjectHeader>(gem.Header);
            transcript.Record("weenieFlags", header.WeenieHeaderFlags);
            transcript.Record("weenieFlags2", header.WeenieHeaderFlags2);
            transcript.Record("useRadius", gem.UseRadius);
            Assert.Equal(0x00200000u, header.WeenieHeaderFlags);
            Assert.Equal(0x00000001u, header.WeenieHeaderFlags2);
            Assert.Equal(0u, header.PhysicsDescriptionFlags);
            Assert.Null(header.Scale);
            Assert.Equal(0.5f, gem.UseRadius);

            // An item the client holds only in its record of the packs, with
            // no body anywhere to ask: the description's radius is the one.
            arm.Runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
            {
                ObjectId = Pebble,
                Type = ItemType.Misc,
                Name = "Pebble",
                ContainerId = ParityWorld.Player,
                StackSize = 1,
                Header = new ClientObjectHeader(
                    0x00200000u, null, 0u, 0u, 0u, null, null, null, null, null, 0.75f),
            });
            PluginInventoryItem pebble = Assert.Single(
                arm.Host.Automation.Items.CaptureOwnedItems(),
                static item => item.ObjectId == Pebble);
            transcript.Record("packedUseRadius", pebble.UseRadius);
            Assert.Equal(0.75f, pebble.UseRadius);
        });

    private const uint Pebble = 0x50000093u;
}
