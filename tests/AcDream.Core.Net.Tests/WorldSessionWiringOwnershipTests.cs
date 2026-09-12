using System.Net;
using System.Reflection;
using System.Buffers.Binary;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Spells;

namespace AcDream.Core.Net.Tests;

public sealed class WorldSessionWiringOwnershipTests
{
    [Fact]
    public void ObjectTableWiring_DisposeDetachesEveryExactSessionDelegate()
    {
        using var session = NewSession();
        var table = new ClientObjectTable();
        var player = new LocalPlayerState();

        IDisposable registration = ObjectTableWiring.Wire(
            session,
            table,
            playerGuid: () => 0x50000001u,
            localPlayer: player);

        Assert.Equal(1, HandlerCount(session, nameof(session.ObjectIntPropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.PlayerInt64PropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.StackSizeUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.InventoryObjectRemoved)));
        Assert.Equal(1, HandlerCount(session, nameof(session.EntityDescriptionRefreshed)));
        Assert.Equal(1, HandlerCount(session, nameof(session.ObjectDataIdPropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.PlayerDataIdPropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.ObjectInstanceIdPropertyUpdated)));
        Assert.Equal(1, HandlerCount(session, nameof(session.PlayerInstanceIdPropertyUpdated)));

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(0, HandlerCount(session, nameof(session.ObjectIntPropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.PlayerInt64PropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.StackSizeUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.InventoryObjectRemoved)));
        Assert.Equal(0, HandlerCount(session, nameof(session.EntityDescriptionRefreshed)));
        Assert.Equal(0, HandlerCount(session, nameof(session.ObjectDataIdPropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.PlayerDataIdPropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.ObjectInstanceIdPropertyUpdated)));
        Assert.Equal(0, HandlerCount(session, nameof(session.PlayerInstanceIdPropertyUpdated)));
    }

    [Fact]
    public void CombatStateWiring_DisposeDetachesOnlyItsExactDelegate()
    {
        using var session = NewSession();
        var combat = new CombatState();
        Action<WorldSession.PlayerIntPropertyUpdate> predecessor = _ => { };
        session.PlayerIntPropertyUpdated += predecessor;
        IDisposable registration = CombatStateWiring.Wire(session, combat);

        Assert.Equal(2, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));

        registration.Dispose();
        registration.Dispose();

        Assert.Equal(1, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));
        session.PlayerIntPropertyUpdated -= predecessor;
    }

    [Fact]
    public void CombatStateWiring_ConcurrentDisposeDetachesExactlyOnce()
    {
        using var session = NewSession();
        IDisposable registration = CombatStateWiring.Wire(session, new CombatState());

        Parallel.For(0, 64, _ => registration.Dispose());

        Assert.Equal(0, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));
    }

    [Fact]
    public void GameEventWiring_DisposeRestoresPreexistingHandlerAndRemovesOwnedSet()
    {
        var dispatcher = new GameEventDispatcher();
        int predecessorCalls = 0;
        dispatcher.Register(GameEventType.Tell, _ => predecessorCalls++);

        IDisposable registration = GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog());
        int wiredCount = dispatcher.RegisteredHandlerCount;
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));
        Assert.Equal(0, predecessorCalls);

        registration.Dispose();
        registration.Dispose();
        dispatcher.Dispatch(new GameEventEnvelope(0, 0, GameEventType.Tell, default));

        Assert.True(wiredCount > 1);
        Assert.Equal(1, dispatcher.RegisteredHandlerCount);
        Assert.Equal(1, predecessorCalls);
    }

    [Fact]
    public void GameEventWiring_OnWorldSession_RestoresInternalRegistrations()
    {
        using var session = NewSession();
        int baselineCount = session.GameEvents.RegisteredHandlerCount;

        IDisposable registration = GameEventWiring.WireAll(
            session.GameEvents,
            new ClientObjectTable(),
            new CombatState(),
            new Spellbook(),
            new ChatLog(),
            turbineChat: new TurbineChatState());

        Assert.True(session.GameEvents.RegisteredHandlerCount > baselineCount);
        registration.Dispose();

        Assert.Equal(baselineCount, session.GameEvents.RegisteredHandlerCount);
    }

    [Fact]
    public void GameEventWiring_AcceptanceGateSilencesCopiedOrInFlightDispatch()
    {
        var dispatcher = new GameEventDispatcher();
        var combat = new CombatState();
        bool accepting = true;
        using IDisposable registration = GameEventWiring.WireAll(
            dispatcher,
            new ClientObjectTable(),
            combat,
            new Spellbook(),
            new ChatLog(),
            accepting: () => accepting);
        byte[] first = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(first, 0x50000001u);
        BinaryPrimitives.WriteSingleLittleEndian(first.AsSpan(4), 0.75f);
        byte[] stale = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(stale, 0x50000001u);
        BinaryPrimitives.WriteSingleLittleEndian(stale.AsSpan(4), 0.25f);

        dispatcher.Dispatch(new GameEventEnvelope(
            0u, 0u, GameEventType.UpdateHealth, first));
        accepting = false;
        dispatcher.Dispatch(new GameEventEnvelope(
            0u, 0u, GameEventType.UpdateHealth, stale));

        Assert.Equal(0.75f, combat.GetHealthPercent(0x50000001u));
    }

    private static WorldSession NewSession() =>
        new(new IPEndPoint(IPAddress.Loopback, 9));

    private static int HandlerCount(WorldSession session, string eventName)
    {
        FieldInfo field = typeof(WorldSession).GetField(
            eventName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"event field {eventName} not found");
        return (field.GetValue(session) as MulticastDelegate)?
            .GetInvocationList()
            .Length ?? 0;
    }
}
