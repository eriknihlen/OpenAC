using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Plugins;
using AcDream.Runtime.Tests.Support;

namespace AcDream.Runtime.Tests.Plugins;

/// <summary>
/// A summoner finds its essences by what the client is actually sent. The
/// server keeps the pet an essence summons to itself; what reaches the client
/// is the shared cooldown every essence belongs to, with the item and again
/// in an appraisal. These pin the item snapshot to that, and pin the count of
/// pets the character owns to the owner the server names on each pet.
/// </summary>
public sealed class RuntimeInventoryItemPetDeviceTests
{
    private const uint ItemId = 0x70005101u;
    private const uint PetId = 0x80005102u;
    private const uint OtherPetId = 0x80005103u;
    private const uint SomeoneElse = 0x50009999u;

    [Fact]
    public void AnEssenceSentWithTheSummoningCooldownIsAPetDevice()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static staged =>
        {
            staged.CooldownId = 213u;
            staged.CooldownDuration = 45d;
        });

        Assert.True(item.IsPetDevice);
        Assert.Equal(0, item.PetClass);
        Assert.Equal(PluginInventoryItem.SummoningCooldownId, item.SharedCooldownId);
        Assert.Equal(45d, item.CooldownSeconds);
    }

    [Fact]
    public void AnItemOnAnyOtherSharedCooldownIsNotAPetDevice()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static staged =>
        {
            staged.CooldownId = 100u;
            staged.CooldownDuration = 30d;
        });

        Assert.False(item.IsPetDevice);
        Assert.Equal(100u, item.SharedCooldownId);
    }

    [Fact]
    public void AnItemWithNoCooldownReadsInert()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static _ => { });

        Assert.False(item.IsPetDevice);
        Assert.Equal(0u, item.SharedCooldownId);
        Assert.Equal(0d, item.CooldownSeconds);
    }

    [Fact]
    public void TheCooldownAnAppraisalOrUpdateSendsMakesItAPetDevice()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(
            host,
            static _ => { },
            static objects => objects.UpdateIntProperty(
                ItemId, (uint)PropertyInt.SharedCooldown, 213));

        Assert.True(item.IsPetDevice);
        Assert.Equal(213u, item.SharedCooldownId);
    }

    [Fact]
    public void APetClassFromTheServerStillMarksAPetDevice()
    {
        using var host = new NoWindowGameRuntimeHost();
        PluginInventoryItem item = CaptureOwned(host, static staged =>
            staged.Properties.Ints[(uint)PropertyInt.PetClass] = 7);

        Assert.True(item.IsPetDevice);
    }

    [Fact]
    public void ThePetsCountedAreTheLivingCreaturesTheServerSaysTheCharacterOwns()
    {
        using var host = new NoWindowGameRuntimeHost();
        using RuntimeAutomationSurface surface = Start(host);
        GameRuntime runtime = host.Runtime;
        uint player = runtime.PlayerIdentity.ServerGuid;
        IItemAutomation items = surface.Items;
        Assert.Equal(0, items.ActiveOwnedPetCount);

        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = PetId,
            Name = "+Acdream's Golem",
            Type = ItemType.Creature,
            PetOwnerId = player,
        });
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = OtherPetId,
            Name = "Someone else's Golem",
            Type = ItemType.Creature,
            PetOwnerId = SomeoneElse,
        });

        Assert.Equal(1, items.ActiveOwnedPetCount);
    }

    private static RuntimeAutomationSurface Start(NoWindowGameRuntimeHost host)
    {
        host.Start();
        for (int tick = 0; tick < 4; tick++)
            host.Session.Tick();
        Assert.True(host.Runtime.Session.IsInWorld);
        var surface = new RuntimeAutomationSurface();
        RuntimeAutomationBindings.Apply(
            surface,
            host.Runtime,
            new RuntimeAutomationHostCapabilities
            {
                HostName = "a host under test",
                Declared = RuntimeAutomationHostCapabilities.AllCapabilityNames,
            });
        return surface;
    }

    private static PluginInventoryItem CaptureOwned(
        NoWindowGameRuntimeHost host,
        Action<ClientObject> stage,
        Action<ClientObjectTable>? afterwards = null)
    {
        using RuntimeAutomationSurface surface = Start(host);
        GameRuntime runtime = host.Runtime;
        var staged = new ClientObject
        {
            ObjectId = ItemId,
            ContainerId = runtime.PlayerIdentity.ServerGuid,
            Name = "Mud Golem Essence",
        };
        stage(staged);
        runtime.InventoryOwner.Objects.AddOrUpdate(staged);
        afterwards?.Invoke(runtime.InventoryOwner.Objects);

        return Assert.Single(
            surface.CaptureOwnedItems(),
            candidate => candidate.ObjectId == ItemId);
    }
}
