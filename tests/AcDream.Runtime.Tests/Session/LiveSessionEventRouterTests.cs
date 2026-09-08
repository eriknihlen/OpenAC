using System.Buffers.Binary;
using System.Net;
using System.Reflection;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Player;
using AcDream.Core.Properties;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Runtime.Session;
using AcDream.Runtime.Gameplay;

namespace AcDream.Runtime.Tests.Session;

public sealed class LiveSessionEventRouterTests
{
    [Fact]
    public void CreatedRouterPublishesNoHandlersUntilExplicitAttach()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;
        var router = new LiveSessionEventRouter(
            session,
            new LiveEntitySessionSink(
                _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                _ => { }),
            new LiveEnvironmentSessionSink(_ => { }, _ => { }),
            NewInventoryBindings(),
            NewCharacterBindings(),
            NewSocialBindings());

        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);

        router.Attach();
        AssertSessionHandlerCounts(session, multiplier: 1);
        Assert.True(session.GameEvents.RegisteredHandlerCount > baselineGameEvents);

        router.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);
    }

    [Fact]
    public void Dispose_DetachesExactHandlersAndSilencesCopiedDelegates()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;
        var counters = new Counters();
        var combat = new CombatState();
        var chat = new ChatLog();
        var router = NewRouter(session, counters, combat: combat, chat: chat);

        AssertSessionHandlerCounts(session, multiplier: 1);
        Assert.True(session.GameEvents.RegisteredHandlerCount > baselineGameEvents);
        Action<double> copiedTime = EventDelegate<Action<double>>(
            session,
            nameof(session.ServerTimeUpdated));
        Action<uint> copiedTeleport = EventDelegate<Action<uint>>(
            session,
            nameof(session.TeleportStarted));
        Action<WorldSession.PlayerIntPropertyUpdate> copiedPlayerInt =
            EventDelegate<Action<WorldSession.PlayerIntPropertyUpdate>>(
                session,
                nameof(session.PlayerIntPropertyUpdated));
        Action<CombatState.DamageDealt> copiedDamage =
            EventDelegate<CombatState, Action<CombatState.DamageDealt>>(
                combat,
                nameof(combat.DamageDealtAccepted));
        copiedTime(123d);
        copiedTeleport(0x50000001u);
        copiedPlayerInt(new WorldSession.PlayerIntPropertyUpdate(
            CombatStateWiring.CombatModePropertyId,
            (int)CombatMode.Melee));
        copiedDamage(new CombatState.DamageDealt("Drudge", 1u, 5u, 0.1f));
        Assert.Equal(1, counters.ServerTime);
        Assert.Equal(1, counters.Teleport);
        Assert.Equal(CombatMode.Melee, combat.CurrentMode);
        Assert.Equal(1, chat.Count);

        router.Dispose();
        router.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);

        copiedTime(456d);
        copiedTeleport(0x50000002u);
        copiedPlayerInt(new WorldSession.PlayerIntPropertyUpdate(
            CombatStateWiring.CombatModePropertyId,
            (int)CombatMode.Magic));
        copiedDamage(new CombatState.DamageDealt("Drudge", 1u, 7u, 0.2f));
        Assert.Equal(1, counters.ServerTime);
        Assert.Equal(1, counters.Teleport);
        Assert.Equal(CombatMode.Melee, combat.CurrentMode);
        Assert.Equal(1, chat.Count);
    }


    [Fact]
    public void ServerMessage_RoutesThroughAddText_WithWireChatTypeVerbatim_WhenWired()
    {
        using var session = NewSession();
        var chat = new ChatLog();
        var reported = new List<(string Text, RetailLogTextType Type)>();
        var social = new LiveSocialSessionBindings(
            chat,
            new TurbineChatState(),
            new FriendsState(),
            new SquelchState(),
            (text, type) => reported.Add((text, type)));
        var router = new LiveSessionEventRouter(
            session,
            new LiveEntitySessionSink(
                _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                _ => { }, _ => { }, _ => { }, _ => { }, _ => { }, _ => { },
                _ => { }),
            new LiveEnvironmentSessionSink(_ => { }, _ => { }),
            NewInventoryBindings(),
            NewCharacterBindings(),
            social);
        router.Attach();

        EventDelegate<Action<AcDream.Core.Net.Messages.ServerMessage.Parsed>>(
            session, nameof(session.ServerMessageReceived))(
            new AcDream.Core.Net.Messages.ServerMessage.Parsed("You are too encumbered to carry that!", 0x1Au));

        var (text, type) = Assert.Single(reported);
        Assert.Equal("You are too encumbered to carry that!", text);
        Assert.Equal(RetailLogTextType.ClientLocal, type);
        Assert.Equal(0, chat.Count);

        router.Dispose();
    }

    [Fact]
    public void ServerMessage_FallsBackToChatDirectly_WhenNoRouterWired()
    {
        using var session = NewSession();
        var chat = new ChatLog();
        var router = NewRouter(session, new Counters(), chat: chat);

        EventDelegate<Action<AcDream.Core.Net.Messages.ServerMessage.Parsed>>(
            session, nameof(session.ServerMessageReceived))(
            new AcDream.Core.Net.Messages.ServerMessage.Parsed("fallback path", 0x00u));

        Assert.Equal(1, chat.Count);
        Assert.Equal("fallback path", chat.Snapshot()[0].Text);

        router.Dispose();
    }

    [Fact]
    public void PlayerKilled_SuppressesLocalParticipantsAndKeepsBystanderLine()
    {
        const uint self = 0x50000001u;
        const uint other = 0x50000002u;
        const uint third = 0x50000003u;
        using var session = NewSession();
        var chat = new ChatLog();
        var social = new LiveSocialSessionBindings(
            chat,
            new TurbineChatState(),
            new FriendsState(),
            new SquelchState(),
            PlayerGuid: () => self);
        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            NewCharacterBindings(),
            social);
        router.Attach();

        Action<PlayerKilled.Parsed> deliver =
            EventDelegate<Action<PlayerKilled.Parsed>>(
                session,
                nameof(session.PlayerKilledReceived));
        deliver(new PlayerKilled.Parsed("self died", self, other));
        deliver(new PlayerKilled.Parsed("self killed", other, self));
        deliver(new PlayerKilled.Parsed("bystander", other, third));

        ChatEntry entry = Assert.Single(chat.Snapshot());
        Assert.Equal("bystander", entry.Text);
        Assert.Equal(other, entry.SenderGuid);
        Assert.Equal(third, entry.ChannelId);
    }


    [Fact]
    public void FellowshipQuit_RoutesSelfGuidToClearAndOtherGuidToRemove()
    {
        const uint self = 0x50000001u;
        const uint other = 0x50000002u;
        using var session = NewSession();
        var fellowship = new RuntimeFellowshipState();
        var allegiance = new RuntimeAllegianceState();
        try
        {
            fellowship.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
                [
                    new GameEvents.FellowMember(
                        self, 0u, 0u, 1u, 100u, 100u, 100u, 100u, 100u, 100u, 0u, "Self"),
                    new GameEvents.FellowMember(
                        other, 0u, 0u, 1u, 100u, 100u, 100u, 100u, 100u, 100u, 0u, "Other"),
                ],
                "The Fellows",
                LeaderGuid: self,
                ShareXp: true,
                EvenXpSplit: false,
                OpenFellow: true,
                Locked: false,
                Departed: []));

            var router = new LiveSessionEventRouter(
                session,
                NoOpEntitySink(),
                NoOpEnvironmentSink(),
                new LiveInventorySessionBindings(
                    new ClientObjectTable(),
                    PlayerGuid: () => self,
                    OnShortcuts: null,
                    OnUseDone: null,
                    ItemMana: new ItemManaState(),
                    ExternalContainers: new ExternalContainerState()),
                NewCharacterBindings(),
                new LiveSocialSessionBindings(
                    new ChatLog(),
                    new TurbineChatState(),
                    new FriendsState(),
                    new SquelchState(),
                    Fellowship: fellowship,
                    Allegiance: allegiance));
            router.Attach();

            // Someone ELSE quits -- removes exactly that one member.
            session.GameEvents.Dispatch(
                GameEventEnvelope.TryParse(WrapFellowshipQuitEnvelope(other))!.Value);

            Assert.True(fellowship.View.Snapshot.IsInFellowship);
            Assert.Equal(1, fellowship.View.Snapshot.MemberCount);
            Assert.False(fellowship.View.TryGetMember(other, out _));
            Assert.True(fellowship.View.TryGetMember(self, out _));

            session.GameEvents.Dispatch(
                GameEventEnvelope.TryParse(WrapFellowshipQuitEnvelope(self))!.Value);

            Assert.False(fellowship.View.Snapshot.IsInFellowship);
            Assert.Equal(0, fellowship.View.Snapshot.MemberCount);

            router.Dispose();
        }
        finally
        {
            fellowship.Dispose();
            allegiance.Dispose();
        }
    }

    private static byte[] WrapFellowshipQuitEnvelope(uint quitterGuid)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, quitterGuid);
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)GameEventType.FellowshipQuit);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }


    [Fact]
    public void TurbineChat_ResponseWithNonZeroHResult_SurfacesAsSystemMessage()
    {
        using var session = NewSession();
        var chat = new ChatLog();
        var router = NewRouter(session, new Counters(), chat: chat);

        EventDelegate<Action<AcDream.Core.Net.Messages.TurbineChat.Parsed>>(
            session, nameof(session.TurbineChatReceived))(
            new AcDream.Core.Net.Messages.TurbineChat.Parsed(
                AcDream.Core.Net.Messages.TurbineChat.BlobType.ResponseBinary,
                AcDream.Core.Net.Messages.TurbineChat.DispatchType.Unknown,
                0u, 0u, 0u, 0u, 0u,
                new AcDream.Core.Net.Messages.TurbineChat.Payload.Response(
                    ContextId: 5u,
                    ResponseId: 2u,
                    MethodId: 2u,
                    HResult: -1)));

        Assert.Equal(1, chat.Count);
        Assert.Contains(
            "rejected",
            chat.Snapshot()[0].Text,
            StringComparison.OrdinalIgnoreCase);

        router.Dispose();
    }

    [Fact]
    public void TurbineChat_ResponseWithZeroHResult_StaysSilent()
    {
        using var session = NewSession();
        var chat = new ChatLog();
        var router = NewRouter(session, new Counters(), chat: chat);

        EventDelegate<Action<AcDream.Core.Net.Messages.TurbineChat.Parsed>>(
            session, nameof(session.TurbineChatReceived))(
            new AcDream.Core.Net.Messages.TurbineChat.Parsed(
                AcDream.Core.Net.Messages.TurbineChat.BlobType.ResponseBinary,
                AcDream.Core.Net.Messages.TurbineChat.DispatchType.Unknown,
                0u, 0u, 0u, 0u, 0u,
                new AcDream.Core.Net.Messages.TurbineChat.Payload.Response(
                    ContextId: 5u,
                    ResponseId: 2u,
                    MethodId: 2u,
                    HResult: 0)));

        Assert.Equal(0, chat.Count);

        router.Dispose();
    }

    [Fact]
    public void TurbineChat_EventSendToRoom_StillRoutesToChannelBroadcast()
    {
        using var session = NewSession();
        var chat = new ChatLog();
        var router = NewRouter(session, new Counters(), chat: chat);

        EventDelegate<Action<AcDream.Core.Net.Messages.TurbineChat.Parsed>>(
            session, nameof(session.TurbineChatReceived))(
            new AcDream.Core.Net.Messages.TurbineChat.Parsed(
                AcDream.Core.Net.Messages.TurbineChat.BlobType.EventBinary,
                AcDream.Core.Net.Messages.TurbineChat.DispatchType.SendToRoomByName,
                0u, 0u, 0u, 0u, 0u,
                new AcDream.Core.Net.Messages.TurbineChat.Payload.EventSendToRoom(
                    RoomId: 2u,
                    SenderName: "Someone",
                    Message: "hi",
                    ExtraDataSize: 0x0Cu,
                    SenderId: 0x50000001u,
                    HResult: 0,
                    ChatType: 2u)));

        Assert.Equal(1, chat.Count);
        Assert.Equal("hi", chat.Snapshot()[0].Text);

        router.Dispose();
    }

    [Fact]
    public void PlayerDescription_ReplacesOptionsBeforeInvokingOnCharacterOptionsChanged()
    {
        using var session = NewSession();
        var character = new RuntimeCharacterState();
        var observed = new List<(uint Options1, uint Options2, uint LiveOptions1AtCallback)>();

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnCharacterOptionsChanged: (options1, options2) =>
                    observed.Add((options1, options2, character.Options.Options1))),
            NewSocialBindings());
        router.Attach();

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(0x50C4A54Au, 0x00948700u))!.Value);

        var (options1, options2, liveOptions1AtCallback) = Assert.Single(observed);
        Assert.Equal(0x50C4A54Au, options1);
        Assert.Equal(0x00948700u, options2);
        Assert.Equal(0x50C4A54Au, liveOptions1AtCallback);
        Assert.Equal(0x50C4A54Au, character.Options.Options1);
        Assert.Equal(0x00948700u, character.Options.Options2);

        router.Dispose();
    }

    [Fact]
    public void PlayerDescription_TrailerTruncatedReSeed_LeavesWordsAndLatchUnchangedAndDoesNotNotify()
    {
        using var session = NewSession();
        var character = new RuntimeCharacterState();
        var observed = new List<(uint Options1, uint Options2)>();

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnCharacterOptionsChanged: (options1, options2) =>
                    observed.Add((options1, options2))),
            NewSocialBindings());
        router.Attach();

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapPlayerDescriptionEnvelope(0x50C4A54Au, 0x00948700u))!.Value);
        Assert.Single(observed);
        Assert.True(character.Options.HasServerSeed);
        Assert.Equal(0x50C4A54Au, character.Options.Options1);
        Assert.Equal(0x00948700u, character.Options.Options2);

        // Truncated re-seed — options1 reads early (real-looking value),
        // the trailer then throws before options2 is ever read.
        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapTruncatedPlayerDescriptionEnvelope(0xDEADBEEFu))!.Value);

        // No second notification, words untouched, latch still armed
        // (from the earlier GOOD seed, not from this truncated one).
        Assert.Single(observed);
        Assert.True(character.Options.HasServerSeed);
        Assert.Equal(0x50C4A54Au, character.Options.Options1);
        Assert.Equal(0x00948700u, character.Options.Options2);

        router.Dispose();
    }

    [Fact]
    public void PlayerDescription_TrailerTruncatedFirstSeed_LeavesDefaultsAndNeverArmsLatch()
    {
        using var session = NewSession();
        var character = new RuntimeCharacterState();
        var observed = new List<(uint Options1, uint Options2)>();

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnCharacterOptionsChanged: (options1, options2) =>
                    observed.Add((options1, options2))),
            NewSocialBindings());
        router.Attach();

        uint defaultOptions1 = character.Options.Options1;
        uint defaultOptions2 = character.Options.Options2;

        session.GameEvents.Dispatch(
            GameEventEnvelope.TryParse(
                WrapTruncatedPlayerDescriptionEnvelope(0xDEADBEEFu))!.Value);

        Assert.Empty(observed);
        Assert.False(character.Options.HasServerSeed);
        Assert.Equal(defaultOptions1, character.Options.Options1);
        Assert.Equal(defaultOptions2, character.Options.Options2);

        router.Dispose();
    }

    [Fact]
    public void NestedRouters_DisposeOlderFirstLeavesOnlyNewerRouter()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;
        var first = new Counters();
        var second = new Counters();
        var routerA = NewRouter(session, first);
        var routerB = NewRouter(session, second);

        AssertSessionHandlerCounts(session, multiplier: 2);
        EventDelegate<Action<double>>(session, nameof(session.ServerTimeUpdated))(1d);
        Assert.Equal(1, first.ServerTime);
        Assert.Equal(1, second.ServerTime);

        routerA.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 1);
        EventDelegate<Action<double>>(session, nameof(session.ServerTimeUpdated))(2d);
        Assert.Equal(1, first.ServerTime);
        Assert.Equal(2, second.ServerTime);

        routerB.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);
    }

    [Fact]
    public void NestedRouters_DisposeNewerFirstLeavesOnlyOlderRouter()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;
        var first = new Counters();
        var second = new Counters();
        var routerA = NewRouter(session, first);
        var routerB = NewRouter(session, second);

        routerB.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 1);
        EventDelegate<Action<double>>(session, nameof(session.ServerTimeUpdated))(1d);
        Assert.Equal(1, first.ServerTime);
        Assert.Equal(0, second.ServerTime);

        routerA.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);
    }

    [Fact]
    public void ThrowingSink_DoesNotCorruptLaterTeardown()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;
        var router = NewRouter(
            session,
            new Counters { ThrowOnServerTime = true });

        Assert.Throws<InvalidOperationException>(() =>
            EventDelegate<Action<double>>(
                session,
                nameof(session.ServerTimeUpdated))(1d));

        router.Dispose();
        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);
    }

    [Fact]
    public void ConstructionFailure_UnwindsEveryPriorRegistration()
    {
        using var session = NewSession();
        int baselineGameEvents = session.GameEvents.RegisteredHandlerCount;

        Assert.Throws<InvalidOperationException>(() => NewRouter(
            session,
            new Counters(),
            step =>
            {
                if (step == 20)
                    throw new InvalidOperationException("injected construction failure");
            }));

        AssertSessionHandlerCounts(session, multiplier: 0);
        Assert.Equal(baselineGameEvents, session.GameEvents.RegisteredHandlerCount);
    }


    [Fact]
    public void ObjectTablePropertyChange_RecomputesAndPushesBurden()
    {
        using var session = NewSession();
        const uint playerGuid = 0x50000001u;
        var objects = new ClientObjectTable();
        var character = new RuntimeCharacterState();
        int movementStatsUpdated = 0;

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            new LiveInventorySessionBindings(
                objects,
                PlayerGuid: () => playerGuid,
                OnShortcuts: null,
                OnUseDone: null,
                ItemMana: new ItemManaState(),
                ExternalContainers: new ExternalContainerState()),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnMovementStatsUpdated: () => movementStatsUpdated++),
            NewSocialBindings());

        router.Attach();

        character.LocalPlayer.OnAttributeUpdate(
            atType: 1u, ranks: 90u, start: 10u, xp: 0u);
        Assert.Equal(1, movementStatsUpdated);
        Assert.Equal(0f, character.MovementSkills.Burden, precision: 4);

        // EncumbranceVal (property 5) = 7500 -> load = 7500/15000 = 0.5.
        var props = new PropertyBundle();
        props.Ints[(uint)PropertyInt.EncumbranceVal] = 7500;
        objects.UpsertProperties(playerGuid, props);

        Assert.True(movementStatsUpdated >= 2);
        Assert.Equal(0.5f, character.MovementSkills.Burden, precision: 4);

        router.Dispose();
    }

    [Fact]
    public void StrengthEnchantmentChange_RecomputesBurdenWithoutBaseAttributeUpdate()
    {
        using var session = NewSession();
        const uint playerGuid = 0x50000001u;
        var objects = new ClientObjectTable();
        var character = new RuntimeCharacterState();
        character.InstallSpellMetadata(SpellTable.LoadFromReader(new System.IO.StringReader(
            "Spell ID,Name,Flags [Hex]\n42,Strength Test,0x4\n")));
        int movementStatsUpdated = 0;

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            new LiveInventorySessionBindings(
                objects,
                PlayerGuid: () => playerGuid,
                OnShortcuts: null,
                OnUseDone: null,
                ItemMana: new ItemManaState(),
                ExternalContainers: new ExternalContainerState()),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnMovementStatsUpdated: () => movementStatsUpdated++),
            NewSocialBindings());

        router.Attach();
        character.LocalPlayer.OnAttributeUpdate(
            atType: 1u, ranks: 90u, start: 10u, xp: 0u);
        var props = new PropertyBundle();
        props.Ints[(uint)PropertyInt.EncumbranceVal] = 16500;
        objects.UpsertProperties(playerGuid, props);
        Assert.Equal(1.1f, character.MovementSkills.Burden, precision: 4);

        int beforeBuff = movementStatsUpdated;
        character.Spellbook.OnEnchantmentAdded(new ActiveEnchantmentRecord(
            SpellId: 42u,
            LayerId: 1u,
            Duration: 60d,
            CasterGuid: playerGuid,
            StatModType: (uint)EnchantmentMath.EnchantmentTypeFlag.Attribute,
            StatModKey: 1u,
            StatModValue: 1.2f,
            Bucket: 1u));

        Assert.Equal(120, character.LocalPlayer.GetEffectiveAttribute(
            LocalPlayerState.AttributeKind.Strength));
        Assert.Equal(16500f / 18000f, character.MovementSkills.Burden, precision: 4);
        Assert.Equal(beforeBuff + 1, movementStatsUpdated);

        int beforePurge = movementStatsUpdated;
        character.Spellbook.OnPurgeAll();

        Assert.Equal(100, character.LocalPlayer.GetEffectiveAttribute(
            LocalPlayerState.AttributeKind.Strength));
        Assert.Equal(1.1f, character.MovementSkills.Burden, precision: 4);
        Assert.Equal(beforePurge + 1, movementStatsUpdated);

        router.Dispose();
    }

    [Fact]
    public void ObjectTablePropertyChange_RecomputesMovementSkillAugmentations()
    {
        using var session = NewSession();
        const uint playerGuid = 0x50000001u;
        var objects = new ClientObjectTable();
        var character = new RuntimeCharacterState();
        character.LocalPlayer.OnSkillUpdate(
            RuntimeCharacterState.RunSkillId,
            ranks: 0u,
            status: 3u,
            xp: 0u,
            init: 0u,
            resistance: 0u,
            lastUsed: 0d,
            formulaBonus: 200u);
        character.UpdateMovementSkillBase(
            runSkillBase: 200,
            jumpSkillBase: -1);

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            new LiveInventorySessionBindings(
                objects,
                PlayerGuid: () => playerGuid,
                OnShortcuts: null,
                OnUseDone: null,
                ItemMana: new ItemManaState(),
                ExternalContainers: new ExternalContainerState()),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d),
            NewSocialBindings());
        router.Attach();

        var properties = new PropertyBundle();
        properties.Ints[(uint)PropertyInt.LumAugAllSkills] = 2;
        properties.Ints[(uint)PropertyInt.AugmentationJackOfAllTrades] = 1;
        properties.Ints[(uint)PropertyInt.LumAugSkilledSpec] = 3;
        objects.UpsertProperties(playerGuid, properties);

        Assert.Equal(213, character.MovementSkills.RunSkill);
        router.Dispose();
    }

    [Fact]
    public void StaminaVitalChange_PushesCurrentStamina()
    {
        using var session = NewSession();
        var character = new RuntimeCharacterState();
        int movementStatsUpdated = 0;

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: null,
                OnSkillsUpdated: null,
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnMovementStatsUpdated: () => movementStatsUpdated++),
            NewSocialBindings());

        router.Attach();
        Assert.Equal(-1, character.MovementSkills.CurrentStamina);

        character.LocalPlayer.OnVitalUpdate(
            vitalId: 8u, ranks: 40u, start: 20u, xp: 0u, current: 45u);

        Assert.Equal(1, movementStatsUpdated);
        Assert.Equal(45, character.MovementSkills.CurrentStamina);

        router.Dispose();
    }

    [Fact]
    public void AttributeAndSkillUpdates_RouteToPlayerStateAndMovementSeam()
    {
        using var session = NewSession();
        var character = new RuntimeCharacterState();
        var skillsPushed = new List<(int Run, int Jump)>();
        int movementStatsUpdated = 0;

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            NewInventoryBindings(),
            new LiveCharacterSessionBindings(
                new CombatState(),
                character,
                ResolveSkillFormulaBonus: (skillId, attrs) =>
                    skillId == 24u && attrs.TryGetValue(3u, out uint quickness)
                        ? quickness / 2u
                        : 0u,
                OnSkillsUpdated: (run, jump) => skillsPushed.Add((run, jump)),
                OnConfirmationRequest: null,
                OnConfirmationDone: null,
                ClientTime: () => 0d,
                OnMovementStatsUpdated: () => movementStatsUpdated++),
            NewSocialBindings());
        router.Attach();

        character.LocalPlayer.OnAttributeUpdate(
            atType: 3u, ranks: 0u, start: 100u, xp: 0u);
        character.LocalPlayer.OnSkillUpdate(
            skillId: 24u, ranks: 10u, status: 2u, xp: 0u,
            init: 5u, resistance: 0u, lastUsed: 0d, formulaBonus: 50u);
        skillsPushed.Clear();
        movementStatsUpdated = 0;

        EventDelegate<Action<PrivateUpdateAttribute.Parsed>>(
            session, nameof(session.AttributeUpdated))
            .Invoke(new PrivateUpdateAttribute.Parsed(
                Sequence: 1, AttributeId: 3u, Ranks: 60u, Start: 100u, Xp: 500u));

        Assert.Equal(160u,
            character.LocalPlayer.GetAttribute(
                LocalPlayerState.AttributeKind.Quickness)?.Current);
        Assert.Equal(80u, character.LocalPlayer.Skills[24u].FormulaBonus);
        // Movement re-applied with the new total: 80 formula + 5 init + 10 ranks.
        Assert.Equal((95, -1), Assert.Single(skillsPushed));
        Assert.Equal(1, movementStatsUpdated);

        // The server's answer to a Run skill raise: ranks 10 -> 11.
        EventDelegate<Action<PrivateUpdateSkill.Parsed>>(
            session, nameof(session.SkillUpdated))
            .Invoke(new PrivateUpdateSkill.Parsed(
                Sequence: 2, SkillId: 24u, Ranks: 11u, AdjustPP: 1,
                AdvancementClass: 2u, Xp: 1000u, Init: 5u,
                Resistance: 0u, LastUsed: 0d));
        Assert.Equal((96, -1), skillsPushed[^1]);
        Assert.Equal(2, movementStatsUpdated);

        // A non-movement skill update must not push movement.
        EventDelegate<Action<PrivateUpdateSkill.Parsed>>(
            session, nameof(session.SkillUpdated))
            .Invoke(new PrivateUpdateSkill.Parsed(
                Sequence: 3, SkillId: 6u, Ranks: 1u, AdjustPP: 1,
                AdvancementClass: 2u, Xp: 0u, Init: 0u,
                Resistance: 0u, LastUsed: 0d));
        Assert.Equal(2, movementStatsUpdated);

        router.Dispose();
    }

    private static LiveEntitySessionSink NoOpEntitySink() => new(
        Spawned: _ => { },
        Deleted: _ => { },
        PickedUp: _ => { },
        MotionUpdated: _ => { },
        PositionUpdated: _ => { },
        VectorUpdated: _ => { },
        StateUpdated: _ => { },
        ParentUpdated: _ => { },
        TeleportStarted: _ => { },
        AppearanceUpdated: _ => { },
        PlayPhysicsScript: _ => { },
        PlayPhysicsScriptType: _ => { },
        SoundEvent: _ => { });

    [Fact]
    public void HouseRentNotices_RouteIntoTheSharedRuntimeOwner()
    {
        const uint self = 0x50000001u;
        using var session = NewSession();
        var objects = new ClientObjectTable();
        objects.AddOrUpdate(new ClientObject { ObjectId = self, Type = ItemType.Creature });
        var house = new RuntimeHouseState(objects);
        house.ApplyHouseData(
            new GameEvents.HouseData(
                BuyTime: 0u,
                RentTime: 1_700_000_000u,
                Type: 1u,
                MaintenanceFree: false,
                Buy: [],
                Rent: [new GameEvents.HousePayment(10, 10, 1u, "Pyreal", "Pyreals")],
                Position: new CreateObject.ServerPosition()),
            self);

        var router = new LiveSessionEventRouter(
            session,
            NoOpEntitySink(),
            NoOpEnvironmentSink(),
            new LiveInventorySessionBindings(
                objects,
                PlayerGuid: () => self,
                OnShortcuts: null,
                OnUseDone: null,
                ItemMana: new ItemManaState(),
                ExternalContainers: new ExternalContainerState()),
            NewCharacterBindings(),
            new LiveSocialSessionBindings(
                new ChatLog(),
                new TurbineChatState(),
                new FriendsState(),
                new SquelchState(),
                House: house));
        router.Attach();

        session.GameEvents.Dispatch(GameEventEnvelope.TryParse(
            WrapGameEvent(
                GameEventType.UpdateRentTime,
                BitConverter.GetBytes(1_710_000_000u)))!.Value);
        Assert.Equal("Rent:\n0/10 Pyreals", house.Lines[1]);
        Assert.Equal(HousePanelTextColor.RentNotPaid, house.PanelLines[^2].Color);

        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(
            payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(1u);
            writer.Write(10);
            writer.Write(10);
            writer.Write(1u);
            WriteString16L(writer, "Pyreal");
            WriteString16L(writer, "Pyreals");
        }
        session.GameEvents.Dispatch(GameEventEnvelope.TryParse(
            WrapGameEvent(GameEventType.UpdateRentPayment, payload.ToArray()))!.Value);

        Assert.Equal("Rent:\n10/10 Pyreals", house.Lines[1]);
        Assert.Equal(HousePanelTextColor.RentPaid, house.PanelLines[^2].Color);
        router.Dispose();
    }

    private static LiveEnvironmentSessionSink NoOpEnvironmentSink() => new(
        EnvironChanged: _ => { },
        ServerTimeUpdated: _ => { });

    private static LiveSessionEventRouter NewRouter(
        WorldSession session,
        Counters counters,
        Action<int>? constructionCheckpoint = null,
        CombatState? combat = null,
        ChatLog? chat = null)
    {
        var router = new LiveSessionEventRouter(
            session,
            new LiveEntitySessionSink(
                Spawned: _ => { },
                Deleted: _ => { },
                PickedUp: _ => { },
                MotionUpdated: _ => { },
                PositionUpdated: _ => { },
                VectorUpdated: _ => { },
                StateUpdated: _ => { },
                ParentUpdated: _ => { },
                TeleportStarted: _ => counters.Teleport++,
                AppearanceUpdated: _ => { },
                PlayPhysicsScript: _ => { },
                PlayPhysicsScriptType: _ => { },
                SoundEvent: _ => { }),
            new LiveEnvironmentSessionSink(
                EnvironChanged: _ => { },
                ServerTimeUpdated: _ =>
                {
                    if (counters.ThrowOnServerTime)
                        throw new InvalidOperationException("injected sink failure");
                    counters.ServerTime++;
                }),
            NewInventoryBindings(),
            NewCharacterBindings(combat),
            NewSocialBindings(chat),
            constructionCheckpoint);
        try
        {
            router.Attach();
            return router;
        }
        catch
        {
            router.Dispose();
            throw;
        }
    }

    private static LiveInventorySessionBindings NewInventoryBindings() => new(
        new ClientObjectTable(),
        PlayerGuid: () => 0x50000001u,
        OnShortcuts: null,
        OnUseDone: null,
        ItemMana: new ItemManaState(),
        ExternalContainers: new ExternalContainerState());

    private static LiveCharacterSessionBindings NewCharacterBindings(
        CombatState? combat = null) => new(
        combat ?? new CombatState(),
        new RuntimeCharacterState(),
        ResolveSkillFormulaBonus: null,
        OnSkillsUpdated: null,
        OnConfirmationRequest: null,
        OnConfirmationDone: null,
        ClientTime: () => 0d);

    private static LiveSocialSessionBindings NewSocialBindings(
        ChatLog? chat = null) => new(
        chat ?? new ChatLog(),
        new TurbineChatState(),
        new FriendsState(),
        new SquelchState());

    private static byte[] WrapPlayerDescriptionEnvelope(uint options1, uint options2)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);       // property flags
            writer.Write(0x52u);    // player weenie type
            writer.Write(0u);       // vector flags
            writer.Write(0u);       // has health
            writer.Write(0x40u);
            writer.Write(options1);
            writer.Write(0u);
            writer.Write(0u);       // spellbook filters
            writer.Write(options2);
            writer.Write(0u);
            writer.Write(0u);
        }

        byte[] payload = stream.ToArray();
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)GameEventType.PlayerDescription);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static byte[] WrapTruncatedPlayerDescriptionEnvelope(uint options1)
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(0u);           // property flags
            writer.Write(0x52u);        // player weenie type
            writer.Write(0u);           // vector flags
            writer.Write(0u);           // has health
            writer.Write(0x01u);        // option flags: Shortcut
            writer.Write(options1);
            writer.Write(1_000_000u);
        }

        byte[] payload = stream.ToArray();
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)GameEventType.PlayerDescription);
        Array.Copy(payload, 0, body, GameEventEnvelope.HeaderSize, payload.Length);
        return body;
    }

    private static byte[] WrapGameEvent(GameEventType type, byte[] payload)
    {
        byte[] body = new byte[GameEventEnvelope.HeaderSize + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(body, GameEventEnvelope.Opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8), 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)type);
        payload.CopyTo(body, GameEventEnvelope.HeaderSize);
        return body;
    }

    private static void WriteString16L(BinaryWriter writer, string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static WorldSession NewSession() =>
        new(new IPEndPoint(IPAddress.Loopback, 9));

    private static void AssertSessionHandlerCounts(
        WorldSession session,
        int multiplier)
    {
        string[] directEvents =
        [
            nameof(session.EntitySpawned),
            nameof(session.EntityDeleted),
            nameof(session.EntityPickedUp),
            nameof(session.MotionUpdated),
            nameof(session.PositionUpdated),
            nameof(session.VectorUpdated),
            nameof(session.StateUpdated),
            nameof(session.ParentUpdated),
            nameof(session.TeleportStarted),
            nameof(session.AppearanceUpdated),
            nameof(session.PlayPhysicsScriptReceived),
            nameof(session.PlayPhysicsScriptTypeReceived),
            nameof(session.EnvironChanged),
            nameof(session.ServerTimeUpdated),
            nameof(session.SpeechHeard),
            nameof(session.ServerMessageReceived),
            nameof(session.EmoteHeard),
            nameof(session.SoulEmoteHeard),
            nameof(session.PlayerKilledReceived),
            nameof(session.TurbineChatReceived),
            nameof(session.VitalUpdated),
            nameof(session.VitalCurrentUpdated),
            nameof(session.AttributeUpdated),
            nameof(session.SkillUpdated),
        ];
        foreach (string eventName in directEvents)
            Assert.Equal(multiplier, HandlerCount(session, eventName));

        Assert.Equal(multiplier, HandlerCount(session, nameof(session.ObjectIntPropertyUpdated)));
        Assert.Equal(multiplier * 2, HandlerCount(session, nameof(session.PlayerIntPropertyUpdated)));
        Assert.Equal(multiplier, HandlerCount(session, nameof(session.PlayerInt64PropertyUpdated)));
        Assert.Equal(multiplier, HandlerCount(session, nameof(session.StackSizeUpdated)));
        Assert.Equal(multiplier, HandlerCount(session, nameof(session.InventoryObjectRemoved)));
    }

    private static int HandlerCount(WorldSession session, string eventName) =>
        (EventField(session, eventName).GetValue(session) as MulticastDelegate)?
            .GetInvocationList()
            .Length ?? 0;

    private static TDelegate EventDelegate<TDelegate>(
        WorldSession session,
        string eventName)
        where TDelegate : Delegate =>
        Assert.IsType<TDelegate>(EventField(session, eventName).GetValue(session));

    private static TDelegate EventDelegate<TOwner, TDelegate>(
        TOwner owner,
        string eventName)
        where TOwner : class
        where TDelegate : Delegate =>
        Assert.IsType<TDelegate>(
            typeof(TOwner).GetField(
                eventName,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner));

    private static FieldInfo EventField(WorldSession session, string eventName) =>
        typeof(WorldSession).GetField(
            eventName,
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"event field {eventName} not found");

    private sealed class Counters
    {
        public int ServerTime;
        public int Teleport;
        public bool ThrowOnServerTime;
    }
}
