using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// Letting go of a carried item the server no longer has. The client drops
/// such an item only when a plugin asks and only under guards that keep a
/// plugin from wiping real inventory: the server must have just refused to
/// appraise it, it must be carried and not worn, a pack must be empty, and
/// the item must have the server record a server delete would remove.
///
/// Mutation check, run 2026-09-25: dropping the "server refused to
/// appraise" guard turns <see cref="AnItemTheServerStillDescribesIsKept"/>
/// red, dropping the worn guard turns
/// <see cref="AWornItemIsKept"/> red, and dropping the contents guard turns
/// <see cref="APackThatListsContentsIsKept"/> red. Dropping the
/// awaited-appraisal guard turns
/// <see cref="AnItemWhoseAppraisalIsStillAwaitedIsNotLetGoOfYet"/> red.
/// Dropping the recency guard, or the runtime clock the object table stamps
/// a refusal with, turns
/// <see cref="ARefusalThatIsNoLongerRecentIsNotActedOnUntilTheServerRefusesAgain"/>
/// red; no longer clearing the refusal on a stack-size update turns
/// <see cref="AnItemTheServerSpeaksOfAfterRefusingItIsKept"/> red.
/// <see cref="AWornItemIsKept"/> sets both marks of a worn item and so stays
/// green when either half of the worn guard is dropped; each half on its own
/// turns one case of <see cref="AnItemWornByEitherMarkIsKept"/> red. Reading
/// only the pack's listing turns
/// <see cref="APackSomethingStillNamesAsItsContainerIsKept"/> red.
/// </summary>
public sealed class RuntimeForgetStaleItemTests
{
    private const uint Item = 0x70006001u;
    private const uint Inside = 0x70006002u;

    [Fact]
    public void ARefusedCarriedItemLeavesTheClientTheWayAServerDeleteRemovesIt()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        int removed = 0;
        runtime.InventoryOwner.Objects.ObjectRemoved += item =>
        {
            if (item.ObjectId == Item)
                removed++;
        };
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Completed, result.Status);
        Assert.Null(runtime.InventoryOwner.Objects.Get(Item));
        Assert.False(runtime.EntityObjects.Entities.TryGetActive(Item, out _));
        Assert.DoesNotContain(
            runtime.InventoryOwner.Objects.GetContents(
                runtime.PlayerIdentity.ServerGuid),
            id => id == Item);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void AnItemTheServerStillDescribesIsKept()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    /// <summary>
    /// A caller that asks a second time is waiting for that answer, so an
    /// earlier refusal does not count while the new appraisal is on its way:
    /// the answer may yet say the item exists.
    /// </summary>
    [Fact]
    public void AnItemWhoseAppraisalIsStillAwaitedIsNotLetGoOfYet()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);
        Assert.True(runtime.ActionOwner.Transactions.TryRequestAppraisal(
            Item,
            static _ => { },
            AcDream.Runtime.Gameplay.AppraisalRequestOrigin.Automation));
        Assert.False(surface.Items.IsBusy);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Busy, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    /// <summary>
    /// Only a refusal the caller has just seen counts. An old one may be the
    /// repeat-request refusal of a real item nobody asked about since; asking
    /// again renews it, on the runtime's own clock.
    /// </summary>
    [Fact]
    public void ARefusalThatIsNoLongerRecentIsNotActedOnUntilTheServerRefusesAgain()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);
        runtime.Clock.Advance(
            RuntimeAutomationSurface.StaleItemRefusalWindowSeconds + 1d);

        PluginItemCommandResult old = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, old.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));

        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);
        PluginItemCommandResult renewed = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Completed, renewed.Status);
        Assert.Null(runtime.InventoryOwner.Objects.Get(Item));
    }

    /// <summary>
    /// Anything the server says about the item after refusing it shows the
    /// server still has it, so the refusal no longer counts.
    /// </summary>
    [Fact]
    public void AnItemTheServerSpeaksOfAfterRefusingItIsKept()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);
        runtime.InventoryOwner.Objects.UpdateStackSize(Item, 2, 10);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    [Fact]
    public void AWornItemIsKept()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        uint player = runtime.PlayerIdentity.ServerGuid;
        Carry(runtime, Item, player);
        ClientObject item = runtime.InventoryOwner.Objects.Get(Item)!;
        item.WielderId = player;
        item.CurrentlyEquippedLocation = EquipMask.HeadWear;
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    /// <summary>
    /// Either half of "worn" is enough on its own: an item in an equipment
    /// slot the client has no wielder for, and an item with a wielder but no
    /// slot, are both on the character.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AnItemWornByEitherMarkIsKept(bool inASlot, bool wielded)
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        uint player = runtime.PlayerIdentity.ServerGuid;
        Carry(runtime, Item, player);
        ClientObject item = runtime.InventoryOwner.Objects.Get(Item)!;
        item.WielderId = wielded ? player : 0u;
        item.CurrentlyEquippedLocation = inASlot ? EquipMask.HeadWear : EquipMask.None;
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    /// <summary>
    /// A pack's listing is only what the server last showed of it. An object
    /// that still names the pack as its container, although the latest
    /// listing left it out, keeps the pack: letting go of the pack would
    /// strand the object under a container that no longer exists.
    /// </summary>
    [Fact]
    public void APackSomethingStillNamesAsItsContainerIsKept()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        Carry(runtime, Inside, Item);
        runtime.InventoryOwner.Objects.ReplaceContents(Item, Array.Empty<uint>());
        Assert.Empty(runtime.InventoryOwner.Objects.GetContents(Item));
        Assert.Equal(Item, runtime.InventoryOwner.Objects.Get(Inside)!.ContainerId);
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Inside));
    }

    [Fact]
    public void APackThatListsContentsIsKept()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        Carry(runtime, Item, runtime.PlayerIdentity.ServerGuid);
        Carry(runtime, Inside, Item);
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Inside));
    }

    /// <summary>
    /// An item the client lists without ever having been told of it by the
    /// server has nothing a server delete would take away, so it is not
    /// taken away here either.
    /// </summary>
    [Fact]
    public void AnItemWithNoServerRecordIsNotAnItemToLetGoOf()
    {
        using var host = new NoWindowGameRuntimeHost();
        (GameRuntime runtime, RuntimeAutomationSurface surface) = Enter(host);
        using RuntimeAutomationSurface owned = surface;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = Item,
            ContainerId = runtime.PlayerIdentity.ServerGuid,
            Name = "a listed item",
        });
        runtime.InventoryOwner.Objects.RecordUnsuccessfulAppraisal(Item);

        PluginItemCommandResult result = surface.Items.ForgetStaleItem(Item);

        Assert.Equal(PluginItemCommandStatus.InvalidItem, result.Status);
        Assert.NotNull(runtime.InventoryOwner.Objects.Get(Item));
    }

    [Fact]
    public void NothingIsLetGoOfBeforeTheSurfaceIsBound()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            surface.Items.ForgetStaleItem(Item).Status);
    }

    private static (GameRuntime, RuntimeAutomationSurface) Enter(
        NoWindowGameRuntimeHost host)
    {
        host.Start();
        for (int tick = 0; tick < 4; tick++)
            host.Session.Tick();
        GameRuntime runtime = host.Runtime;
        Assert.True(runtime.Session.IsInWorld);
        var surface = new RuntimeAutomationSurface();
        RuntimeAutomationBindings.Apply(
            surface,
            runtime,
            new RuntimeAutomationHostCapabilities
            {
                HostName = "a host under test",
                Declared = RuntimeAutomationHostCapabilities.AllCapabilityNames,
            });
        return (runtime, surface);
    }

    /// <summary>
    /// A carried item as the server creates one: no place in the world, a
    /// container, and a server record behind it.
    /// </summary>
    private static void Carry(GameRuntime runtime, uint guid, uint containerId)
    {
        var spawn = new WorldSession.EntitySpawn(
            guid,
            null,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "a carried item",
            null,
            null,
            null,
            PhysicsState: 0,
            InstanceSequence: 1,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1) with
        {
            ContainerId = containerId,
        };
        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(spawn)
            .Canonical!;
        Assert.True(runtime.EntityObjects.ApplyAcceptedSpawn(
            record,
            record.CreateIntegrationVersion,
            record.Snapshot,
            replaceGeneration: false));
    }
}
