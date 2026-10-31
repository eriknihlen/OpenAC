using AcDream.Core.Items;
using AcDream.Core.Ui;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeSelectionCycleTests
{
    private const uint Player = 0x50000001u;
    private const uint Landblock = 0x01010001u;

    // A character hidden from the UI (bit 0x80 of the description) is never
    // a candidate for the person keys; the top bit of the field is no such
    // mark.
    [Fact]
    public void PersonKeysSkipSomeoneHiddenFromTheUi()
    {
        const uint hidden = 0x50000010u;
        const uint marked = 0x50000011u;
        using GameRuntime runtime = UseRangeTestWorld.Create();
        runtime.PlayerIdentity.ServerGuid = Player;
        AddPerson(runtime, Player, x: 10f, (uint)PublicWeenieFlags.Player);
        AddPerson(
            runtime,
            hidden,
            x: 12f,
            (uint)(PublicWeenieFlags.Player | PublicWeenieFlags.UiHidden));
        AddPerson(
            runtime,
            marked,
            x: 14f,
            (uint)PublicWeenieFlags.Player | 0x8000_0000u);

        RuntimeSelectionCycle cycle = runtime.SelectionCycleOwner;

        Assert.Equal(
            marked,
            cycle.FindPlayer(RuntimeSelectionCycleDirection.Closest, anchor: null));
        Assert.Equal(
            marked,
            cycle.FindPlayer(RuntimeSelectionCycleDirection.Next, anchor: marked));
    }

    private static void AddPerson(
        GameRuntime runtime,
        uint guid,
        float x,
        uint publicFlags)
    {
        UseRangeTestWorld.Add(runtime, guid, Landblock, x, y: 10f);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = guid,
            Type = ItemType.Creature,
            PublicWeenieBitfield = publicFlags,
            RadarBehavior = (byte)RadarBehavior.ShowAlways,
        });
    }
}
