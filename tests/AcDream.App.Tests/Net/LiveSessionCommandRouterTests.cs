using System.Runtime.CompilerServices;
using AcDream.App.Net;
using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net.Messages;
using AcDream.Core.Social;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime;
using AcDream.Runtime.Session;
using AcDream.UI.Abstractions;

namespace AcDream.App.Tests.Net;

public sealed class LiveSessionCommandRouterTests
{
    [Fact]
    public void LoginCommandSequenceUsesTheIdenticalGraphicalCommandSurface()
    {
        using var communication = new RuntimeCommunicationState();
        var calls = new List<string>();
        ClientCommandController.Bindings client = NewClientBindings() with
        {
            QueryAge = () => calls.Add("client:age"),
        };
        var router = NewRouter(
            chat: communication.Chat,
            turbine: communication.TurbineChat,
            communication: communication,
            clientBindings: client,
            sendTalk: text => calls.Add($"talk:{text}"),
            sendTell: (target, text) =>
                calls.Add($"tell:{target}:{text}"),
            sendChannel: (channel, text) =>
                calls.Add($"channel:{channel:X8}:{text}"));
        var surface = new LiveSessionCommandSurface();
        using ILiveSessionCommandRouting lease = surface.Attach(router);
        lease.Activate();
        var sequence = new LoginCommandSequence(
            ["hello", "/tell Bob, secret", "@admin raw", "/age"],
            TimeSpan.Zero,
            new RuntimeChatCommandFeedback(communication),
            surface);

        sequence.EnteredWorld(new RuntimeGenerationToken(9));

        Assert.Equal(
            [
                "talk:hello",
                "tell:Bob:secret",
                "channel:00000002:raw",
                "client:age",
            ],
            calls);
    }

    [Fact]
    public void GraphicalRouteKeepsWireOnlyClientCommandParityWithHeadless()
    {
        var calls = new List<string>();
        ClientCommandController.Bindings client = NewClientBindings() with
        {
            AddPlayerPermission = name =>
                calls.Add($"permit:add:{name}"),
            RemovePlayerPermission = name =>
                calls.Add($"permit:remove:{name}"),
            ModifyGlobalSquelch = (add, messageType) =>
                calls.Add($"squelch:{add}:{messageType}"),
        };
        var router = NewRouter(clientBindings: client);
        router.Activate();

        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.Permit,
            "add Aunt Agatha"));
        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.Permit,
            "remove Lord Gnarly Beard"));
        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.ChatToggle,
            "on"));
        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.ChatToggle,
            "off"));
        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.NoTellToggle,
            "on"));
        router.Publish(new ExecuteClientCommandCmd(
            ClientCommandId.NoTellToggle,
            "off"));

        Assert.Equal(
            [
                "permit:add:Aunt Agatha",
                "permit:remove:Lord Gnarly Beard",
                "squelch:False:2",
                "squelch:True:2",
                "squelch:True:3",
                "squelch:False:3",
            ],
            calls);
    }

    [Fact]
    public void AdministrationBindings_UseTheSameActivationAndDisposalGuard()
    {
        var calls = new List<string>();
        ClientCommandController.Bindings client = NewClientBindings() with
        {
            Administration = NewAdministrationBindings() with
            {
                SetMotd = text => calls.Add("motd:" + text),
                BootSpecificHouseGuest = name => calls.Add("boot:" + name),
            },
        };
        var router = NewRouter(clientBindings: client);

        Publish();
        router.Activate();
        Publish();
        router.Dispose();
        Publish();

        Assert.Equal(["motd:Welcome", "boot:Lord Bob"], calls);

        void Publish()
        {
            router.Publish(new ExecuteClientCommandCmd(
                ClientCommandId.AllegianceMotd,
                "set Welcome"));
            router.Publish(new ExecuteClientCommandCmd(
                ClientCommandId.HouseBoot,
                "Lord Bob"));
        }
    }

    [Fact]
    public void InactiveAndDisposedRouter_CannotReachTransport()
    {
        var sent = new List<string>();
        var router = NewRouter(sendTalk: sent.Add);

        router.Publish(new SendServerCommandCmd("@before"));
        router.Activate();
        router.Activate();
        router.Publish(new SendServerCommandCmd("@active"));
        router.Dispose();
        router.Dispose();
        router.Publish(new SendServerCommandCmd("@after"));

        Assert.Equal(["@active"], sent);
        Assert.False(router.IsActive);
    }

    [Fact]
    public void CommandSurfacePublishesToOneBorrowedRouteAndRetiresStaleRoute()
    {
        var sent = new List<string>();
        var surface = new LiveSessionCommandSurface();
        var first = NewRouter(sendTalk: text => sent.Add($"first:{text}"));
        var firstLease = surface.Attach(first);
        firstLease.Activate();

        surface.Publish(new SendServerCommandCmd("@one"));
        var second = NewRouter(sendTalk: text => sent.Add($"second:{text}"));
        Assert.Throws<InvalidOperationException>(() => surface.Attach(second));

        firstLease.Dispose();
        first.Publish(new SendServerCommandCmd("@stale"));
        var secondLease = surface.Attach(second);
        secondLease.Activate();
        surface.Publish(new SendServerCommandCmd("@two"));
        secondLease.Dispose();

        Assert.Equal(["first:@one", "second:@two"], sent);
        Assert.False(first.IsActive);
        Assert.False(second.IsActive);
    }

    [Fact]
    public void TellAndLegacyChannel_PreserveOutboundAndEchoPolicy()
    {
        var tells = new List<(string Target, string Text)>();
        var channels = new List<(uint Id, string Text)>();
        var chat = new ChatLog();
        var router = NewRouter(
            chat: chat,
            sendTell: (target, text) => tells.Add((target, text)),
            sendChannel: (id, text) => channels.Add((id, text)));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.Tell, "Friend", "hello"));
        router.Publish(new SendChatCmd(ChatChannelKind.Fellowship, null, "group"));

        Assert.Equal([("Friend", "hello")], tells);
        Assert.Equal([(0x00000800u, "group")], channels);
        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void TurbineChannel_UsesRuntimeRoomCookieAndNoOptimisticEcho()
    {
        var turbine = new TurbineChatState();
        turbine.OnChannelsReceived(
            allegianceRoom: 0u,
            generalRoom: 0x70000001u,
            tradeRoom: 0u,
            lfgRoom: 0u,
            roleplayRoom: 0u,
            olthoiRoom: 0u,
            societyRoom: 0u,
            societyCelestialHandRoom: 0u,
            societyEldrytchWebRoom: 0u,
            societyRadiantBloodRoom: 0u);
        var sent = new List<(uint Room, uint Type, uint Dispatch, uint Sender, string Text, uint Cookie)>();
        var chat = new ChatLog();
        var router = NewRouter(
            chat: chat,
            turbine: turbine,
            playerGuid: () => 0x50000001u,
            sendTurbine: (room, type, dispatch, sender, text, cookie) =>
                sent.Add((room, type, dispatch, sender, text, cookie)));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.General, null, "world"));

        Assert.Collection(sent, message =>
        {
            Assert.Equal(0x70000001u, message.Room);
            Assert.Equal(0x02u, message.Type);
            Assert.Equal(0x02u, message.Dispatch);
            Assert.Equal(0x50000001u, message.Sender);
            Assert.Equal("world", message.Text);
            Assert.Equal(1u, message.Cookie);
        });
        Assert.Equal(0, chat.Count);
    }


    [Fact]
    public void TurbineUnavailable_RaisesRetailStringThroughAddText_AndSendsNothing()
    {
        var communication = new RuntimeCommunicationState();
        var sent = new List<string>();
        var router = NewRouter(
            chat: communication.Chat,
            communication: communication,
            turbine: new TurbineChatState(), // never received SetTurbineChatChannels
            sendTurbine: (_, _, _, _, text, _) => sent.Add(text));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.General, null, "hello"));

        Assert.Empty(sent);
        Assert.Equal(1, communication.Chat.Count);
        Assert.Equal(
            "Turbine chat is not available.",
            communication.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void HearOptionOff_RaisesNotListeningRefusal_AndSendsNothing()
    {
        var turbine = new TurbineChatState();
        turbine.OnChannelsReceived(
            0u, 0u, 0u, 0u, roleplayRoom: 0x14u, 0u, 0u, 0u, 0u, 0u);
        var communication = new RuntimeCommunicationState();
        var sent = new List<string>();
        var router = NewRouter(
            chat: communication.Chat,
            communication: communication,
            turbine: turbine,
            sendTurbine: (_, _, _, _, text, _) => sent.Add(text));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.Roleplay, null, "hi"));

        Assert.Empty(sent);
        Assert.Equal(1, communication.Chat.Count);
        Assert.Equal(
            "You are not listening to the Roleplay channel!",
            communication.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void AllegianceTurbineEnabledButNoRoom_RefusesLocally_NeverDowngradesToLegacyChannel()
    {
        var communication = new RuntimeCommunicationState();
        var legacySent = new List<(uint Id, string Text)>();
        var turbineSent = new List<string>();
        var turbine = new TurbineChatState();
        turbine.OnChannelsReceived(
            allegianceRoom: 0u, generalRoom: 0x70000001u, tradeRoom: 0u,
            lfgRoom: 0u, roleplayRoom: 0u, olthoiRoom: 0u, societyRoom: 0u,
            societyCelestialHandRoom: 0u, societyEldrytchWebRoom: 0u,
            societyRadiantBloodRoom: 0u); // Enabled = true, AllegianceRoom stays 0
        var router = NewRouter(
            chat: communication.Chat,
            communication: communication,
            turbine: turbine,
            sendChannel: (id, text) => legacySent.Add((id, text)),
            sendTurbine: (_, _, _, _, text, _) => turbineSent.Add(text));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.Allegiance, null, "guild hi"));

        Assert.Empty(legacySent);
        Assert.Empty(turbineSent);
        Assert.Equal(
            "Turbine chat is not available.",
            communication.Chat.Snapshot()[0].Text);
    }

    [Fact]
    public void AllegianceTurbineNeverStarted_FallsBackToLegacyAllegianceBroadcast()
    {
        var chat = new ChatLog();
        var legacySent = new List<(uint Id, string Text)>();
        var turbineSent = new List<string>();
        var router = NewRouter(
            chat: chat,
            turbine: new TurbineChatState(), // never received SetTurbineChatChannels
            sendChannel: (id, text) => legacySent.Add((id, text)),
            sendTurbine: (_, _, _, _, text, _) => turbineSent.Add(text));
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.Allegiance, null, "guild hi"));

        Assert.Equal([(0x02000000u, "guild hi")], legacySent);
        Assert.Empty(turbineSent);
        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void AllegianceBroadcast_AbVerbChannel_RoutesLegacyWithNoLocalEcho()
    {
        var chat = new ChatLog();
        var legacySent = new List<(uint Id, string Text)>();
        var router = NewRouter(
            chat: chat,
            sendChannel: (id, text) => legacySent.Add((id, text)));
        router.Activate();

        router.Publish(new SendChatCmd(
            ChatChannelKind.AllegianceBroadcast, null, "to the whole allegiance"));

        Assert.Equal([(0x02000000u, "to the whole allegiance")], legacySent);
        Assert.Equal(0, chat.Count);
    }


    [Theory]
    [InlineData(ChatChannelKind.Fellowship, 0x00000800u)]
    [InlineData(ChatChannelKind.Vassals, 0x00001000u)]
    [InlineData(ChatChannelKind.Patron, 0x00002000u)]
    [InlineData(ChatChannelKind.Monarch, 0x00004000u)]
    [InlineData(ChatChannelKind.CoVassals, 0x01000000u)]
    public void ServerEchoingLegacyChannels_SkipLocalOptimisticEcho(
        ChatChannelKind kind,
        uint expectedId)
    {
        var chat = new ChatLog();
        var legacySent = new List<(uint Id, string Text)>();
        var router = NewRouter(
            chat: chat,
            sendChannel: (id, text) => legacySent.Add((id, text)));
        router.Activate();

        router.Publish(new SendChatCmd(kind, null, "hi"));

        Assert.Equal([(expectedId, "hi")], legacySent);
        Assert.Equal(0, chat.Count); // no local echo — the server's own
                                     // "" -sender resend is the only echo.
    }

    [Fact]
    public void AllegianceBroadcast_SkipsLocalOptimisticEcho()
    {
        var chat = new ChatLog();
        var router = NewRouter(
            chat: chat,
            sendChannel: (_, _) => { });
        router.Activate();

        router.Publish(new SendChatCmd(
            ChatChannelKind.AllegianceBroadcast, null, "hi"));

        Assert.Equal(0, chat.Count);
    }

    [Fact]
    public void ActivateAfterDispose_IsRejected()
    {
        var router = NewRouter();
        router.Dispose();

        Assert.Throws<ObjectDisposedException>(router.Activate);
    }

    [Trait("Lane", "Timing")]
    [Fact]
    public async Task ConcurrentDispose_WaitsForInFlightTransportThenMakesRouterInert()
    {
        using var sendEntered = new ManualResetEventSlim();
        using var releaseSend = new ManualResetEventSlim();
        var sent = new List<string>();
        var router = NewRouter(sendTalk: text =>
        {
            sendEntered.Set();
            releaseSend.Wait();
            sent.Add(text);
        });
        router.Activate();

        Task publish = Task.Run(() =>
            router.Publish(new SendServerCommandCmd("@active")));
        Assert.True(sendEntered.Wait(TimeSpan.FromSeconds(5)));
        using var disposeStarted = new ManualResetEventSlim();
        Exception? disposeError = null;
        var disposeThread = new Thread(() =>
        {
            disposeStarted.Set();
            try
            {
                router.Dispose();
            }
            catch (Exception error)
            {
                disposeError = error;
            }
        })
        {
            IsBackground = true,
            Name = "LiveSessionCommandRouter concurrent dispose contract",
        };
        disposeThread.Start();
        bool disposeStartedInTime = disposeStarted.Wait(TimeSpan.FromSeconds(5));
        bool disposeBlockedOnTransport = disposeStartedInTime && SpinWait.SpinUntil(
            () => (disposeThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
            TimeSpan.FromSeconds(5));

        releaseSend.Set();
        await publish;
        bool disposeJoined = disposeThread.Join(TimeSpan.FromSeconds(5));
        router.Publish(new SendServerCommandCmd("@after"));

        Assert.True(disposeStartedInTime, "the dedicated dispose thread did not start");
        Assert.True(disposeBlockedOnTransport, "dispose never waited for the in-flight send");
        Assert.True(disposeJoined, "dispose did not finish after the send returned");
        Assert.Null(disposeError);
        Assert.Equal(["@active"], sent);
        Assert.False(router.IsActive);
    }

    [Fact]
    public void ReentrantDisposeFromLog_PreventsLaterTransportAndEcho()
    {
        var turbine = new TurbineChatState();
        turbine.OnChannelsReceived(
            0u, 0x70000001u, 0u, 0u, 0u, 0u, 0u, 0u, 0u, 0u);
        var sent = new List<string>();
        var chat = new ChatLog();
        LiveSessionCommandRouter? router = null;
        router = NewRouter(
            chat: chat,
            turbine: turbine,
            sendTurbine: (_, _, _, _, text, _) => sent.Add(text),
            log: _ => router!.Dispose());
        router.Activate();

        router.Publish(new SendChatCmd(ChatChannelKind.General, null, "world"));

        Assert.Empty(sent);
        Assert.Equal(0, chat.Count);
        Assert.False(router.IsActive);
    }

    [Fact]
    public void RetainedDisposedRouter_ReleasesCapturedTransportGraph()
    {
        (LiveSessionCommandRouter router, WeakReference<object> transport) =
            CreateRouterWithCapturedTransport();

        router.Dispose();
        ForceFullCollection();

        Assert.False(transport.TryGetTarget(out _));
        GC.KeepAlive(router);
    }

    [Fact]
    public void DelayedConfirmationCallback_ReleasesTransportAndBecomesInert()
    {
        (LiveSessionCommandRouter router, WeakReference<object> transport, Action<bool> callback) =
            CreateRouterWithDelayedConfirmation();

        router.Dispose();
        ForceFullCollection();

        Assert.False(transport.TryGetTarget(out _));
        callback(true);
        Assert.False(router.IsActive);
        GC.KeepAlive(callback);
        GC.KeepAlive(router);
    }

    [Fact]
    public void RuntimeStateCommandsUseActiveGenerationRouteAndBecomeInert()
    {
        var shortcuts = new List<ShortcutEntry>();
        var options = new List<(uint OptionId, bool Value)>();
        LiveSessionCommandRouter router = NewRouter(
            addShortcut: shortcuts.Add,
            sendSingleCharacterOption: (id, value) => options.Add((id, value)));

        router.Publish(new AddShortcutRuntimeCmd(
            new ShortcutEntry(1, 0x80000001u, 0u)));
        router.Activate();
        router.Publish(new AddShortcutRuntimeCmd(
            new ShortcutEntry(2, 0x80000002u, 0u)));
        router.Publish(new SetSingleCharacterOptionRuntimeCmd(0x26u, true));
        router.Dispose();
        router.Publish(new AddShortcutRuntimeCmd(
            new ShortcutEntry(3, 0x80000003u, 0u)));

        Assert.Collection(
            shortcuts,
            entry =>
            {
                Assert.Equal(2, entry.Index);
                Assert.Equal(0x80000002u, entry.ObjectId);
            });
        Assert.Equal([(0x26u, true)], options);
    }


    [Fact]
    public void SaveCharacterOptionsCommand_RoutesToBindingOnlyWhileActive()
    {
        int flushes = 0;
        LiveSessionCommandRouter router = NewRouter(
            saveCharacterOptions: () => flushes++);

        router.Publish(new SaveCharacterOptionsRuntimeCmd());
        router.Activate();
        router.Publish(new SaveCharacterOptionsRuntimeCmd());
        router.Dispose();
        router.Publish(new SaveCharacterOptionsRuntimeCmd());

        Assert.Equal(1, flushes);
    }


    [Fact]
    public void SettingsRouteSetSingleCharacterOption_FlipsGateWithoutFreshPlayerDescription()
    {
        var characterState = new RuntimeCharacterState();
        characterState.Options.Replace(characterState.Options.Options1, 0u); // every Options2 bit off — General starts refused
        var turbine = new TurbineChatState();
        turbine.OnChannelsReceived(
            allegianceRoom: 0x10u,
            generalRoom: 0x11u,
            tradeRoom: 0x12u,
            lfgRoom: 0x13u,
            roleplayRoom: 0x14u,
            olthoiRoom: 0x15u,
            societyRoom: 0x16u,
            societyCelestialHandRoom: 0u,
            societyEldrytchWebRoom: 0u,
            societyRadiantBloodRoom: 0u);
        var sent = new List<(uint OptionId, bool Value)>();
        LiveSessionCommandRouter router = NewRouter(
            characterState: characterState,
            sendSingleCharacterOption: (id, value) =>
                characterState.Options.TrySetOption(
                    id,
                    value,
                    sendAutoSave: (sentId, sentValue) => sent.Add((sentId, sentValue))));
        router.Activate();

        Assert.Equal(
            TurbineChatGateStatus.NotListening,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, characterState.Options,
                isOlthoiPlayer: false).Status);

        router.Publish(new SetSingleCharacterOptionRuntimeCmd(
            (uint)CharacterOptionId.ListenToGeneralChat, true));

        Assert.Equal([((uint)CharacterOptionId.ListenToGeneralChat, true)], sent);
        Assert.Equal(
            TurbineChatGateStatus.Allowed,
            TurbineChatMembershipGate.Evaluate(
                ChatChannelKindLite.General, turbine, characterState.Options,
                isOlthoiPlayer: false).Status);
    }

    [Fact]
    public void SettingsRouteLockUi_UpdatesCanonicalBitSynchronouslyAndSendsAutosave()
    {
        var characterState = new RuntimeCharacterState();
        characterState.Options.SetOptionBit(
            (uint)CharacterOptionId.LockUI,
            false);
        var sent = new List<(uint OptionId, bool Value)>();
        LiveSessionCommandRouter router = NewRouter(
            characterState: characterState,
            sendSingleCharacterOption: (id, value) =>
                characterState.Options.TrySetOption(
                    id,
                    value,
                    sendAutoSave: (sentId, sentValue) =>
                        sent.Add((sentId, sentValue))));
        router.Activate();

        router.Publish(new SetSingleCharacterOptionRuntimeCmd(
            (uint)CharacterOptionId.LockUI,
            true));

        Assert.True(characterState.Options.GetOptionBit(CharacterOptionId.LockUI));
        Assert.Equal([((uint)CharacterOptionId.LockUI, true)], sent);

        router.Publish(new SetSingleCharacterOptionRuntimeCmd(
            (uint)CharacterOptionId.LockUI,
            false));

        Assert.False(characterState.Options.GetOptionBit(CharacterOptionId.LockUI));
        Assert.Equal(
        [
            ((uint)CharacterOptionId.LockUI, true),
            ((uint)CharacterOptionId.LockUI, false),
        ], sent);
    }

    [Fact]
    public void ShowWeenieErrorFriendsFull_ResolvesThroughAddText_AndLandsInSpewBoxNotChat()
    {
        var communication = new RuntimeCommunicationState();
        ClientCommandController.Bindings bindings = NewClientBindings() with
        {
            Friends = communication.Friends,
            ShowWeenieError = code =>
            {
                (string? text, RetailLogTextType type) = WeenieErrorMessages.Resolve(code, null);
                if (text is not null)
                    communication.AddText(text, type);
            },
        };
        LiveSessionCommandRouter router = NewRouter(clientBindings: bindings);
        router.Activate();

        var seeded = new List<FriendEntry>();
        for (uint i = 0; i < 50; i++)
            seeded.Add(new FriendEntry(i + 1, $"Friend{i}", true, false, [], []));
        communication.Friends.Apply(new FriendsUpdate(FriendsUpdateType.Full, seeded));

        router.Publish(new ExecuteClientCommandCmd(ClientCommandId.FriendsAdd, "OneTooMany"));

        communication.SpewBox.Tick(0d);
        Assert.Equal(1, communication.SpewBox.Count);
        Assert.Equal(
            "You may only have a maximum of 50 friends at once. If you wish to add more friends, you must first remove some.",
            communication.SpewBox.Snapshot()[0].Text);
        Assert.Equal(0, communication.Chat.Count);
        Assert.Equal(50, communication.Friends.Count);
    }

    private static LiveSessionCommandRouter NewRouter(
        ChatLog? chat = null,
        TurbineChatState? turbine = null,
        Func<uint>? playerGuid = null,
        Action<string>? sendTalk = null,
        Action<string, string>? sendTell = null,
        Action<uint, string>? sendTalkDirect = null,
        Action<uint, string>? sendChannel = null,
        Action<uint, uint, uint, uint, string, uint>? sendTurbine = null,
        Action<ShortcutEntry>? addShortcut = null,
        Action<string>? log = null,
        ClientCommandController.Bindings? clientBindings = null,
        RuntimeCommunicationState? communication = null,
        RuntimeCharacterState? characterState = null,
        Action<uint, bool>? sendSingleCharacterOption = null,
        Action? saveCharacterOptions = null,
        Action<uint>? sendSetTitle = null) => new(
        new LiveSessionCommandBindings(
            clientBindings ?? NewClientBindings(),
            chat ?? new ChatLog(),
            turbine ?? new TurbineChatState(),
            playerGuid ?? (() => 0u),
            sendTalk ?? (_ => { }),
            sendTell ?? ((_, _) => { }),
            sendTalkDirect ?? ((_, _) => { }),
            sendChannel ?? ((_, _) => { }),
            sendTurbine ?? ((_, _, _, _, _, _) => { }),
            AddShortcut: addShortcut ?? (_ => { }),
            RemoveShortcut: _ => { },
            AddFavorite: (_, _, _) => { },
            RemoveFavorite: (_, _) => { },
            SetSpellbookFilter: _ => { },
            ForgetSpell: _ => { },
            SetDesiredComponent: (_, _) => { },
            ClearDesiredComponents: () => { },
            RaiseAttribute: (_, _) => { },
            RaiseVital: (_, _) => { },
            RaiseSkill: (_, _) => { },
            TrainSkill: (_, _) => { },
            AddFriend: _ => { },
            RemoveFriend: _ => { },
            ClearFriends: () => { },
            RequestLegacyFriends: () => { },
            OpenTradeNegotiations: _ => { },
            CloseTradeNegotiations: () => { },
            AddToTrade: _ => { },
            AcceptTrade: (_, _, _) => { },
            DeclineTrade: () => { },
            ResetTrade: () => { },
            ModifyCharacterSquelch: (_, _, _, _) => { },
            ModifyAccountSquelch: (_, _) => { },
            ModifyGlobalSquelch: (_, _) => { },
            Communication: communication ?? new RuntimeCommunicationState(),
            CharacterState: characterState ?? new RuntimeCharacterState(),
            SendSingleCharacterOption: sendSingleCharacterOption ?? ((_, _) => { }),
            SaveCharacterOptions: saveCharacterOptions ?? (() => { }),
            SendSetTitle: sendSetTitle ?? (_ => { }),
            SendFellowshipCreate: (_, _) => { },
            SendFellowshipRecruit: _ => { },
            SendFellowshipDismiss: _ => { },
            SendFellowshipQuit: _ => { },
            SendFellowshipAssignNewLeader: _ => { },
            SendFellowshipChangeOpenness: _ => { },
            SendFellowshipUpdateRequest: _ => { },
            SendAllegianceSwear: _ => { },
            SendAllegianceBreak: _ => { },
            SendAllegianceKick: _ => { },
            SendAllegianceInfoRequest: _ => { },
            SendAllegianceUpdateRequest: _ => { },
            Log: log));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LiveSessionCommandRouter, WeakReference<object>)
        CreateRouterWithCapturedTransport()
    {
        object transport = new object();
        var weak = new WeakReference<object>(transport);
        var router = NewRouter(sendTalk: _ => GC.KeepAlive(transport));
        return (router, weak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LiveSessionCommandRouter, WeakReference<object>, Action<bool>)
        CreateRouterWithDelayedConfirmation()
    {
        object transport = new object();
        var weak = new WeakReference<object>(transport);
        Action<bool>? callback = null;
        ClientCommandController.Bindings bindings = NewClientBindings() with
        {
            ShowConfirmation = (_, completion) => callback = completion,
            Suicide = () => GC.KeepAlive(transport),
        };
        var router = NewRouter(clientBindings: bindings);
        router.Activate();
        router.Publish(new ExecuteClientCommandCmd(ClientCommandId.Die, string.Empty));
        return (router, weak, Assert.IsType<Action<bool>>(callback));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static ClientCommandController.Bindings NewClientBindings() => new(
        TeleportToLifestone: () => { },
        TeleportToMarketplace: () => { },
        TeleportToPkArena: () => { },
        TeleportToPkLiteArena: () => { },
        TeleportToHouse: () => { },
        TeleportToMansion: () => { },
        QueryAge: () => { },
        QueryBirth: () => { },
        ToggleFrameRate: () => { },
        ToggleUiLock: () => { },
        ShowSystemMessage: _ => { },
        ShowClientLocalMessage: _ => { },
        ShowWeenieError: _ => { },
        PlayerPublicWeenieBitfield: () => null,
        ClientVersion: () => "test",
        CurrentPosition: () => null,
        LastOutsideCorpsePosition: () => null,
        ShowConfirmation: (_, _) => { },
        Suicide: () => { },
        ClearChat: _ => { },
        SetChatLogFile: _ => default,
        SaveUi: _ => { },
        LoadUi: _ => { },
        SaveAutoUi: () => { },
        LoadAutoUi: () => { },
        IsAway: () => false,
        SetAway: _ => { },
        SetAwayMessage: _ => { },
        AcceptLootPermits: () => false,
        SetAcceptLootPermits: _ => { },
        DisplayConsent: () => { },
        ClearConsent: () => { },
        RemoveConsent: _ => { },
        SendEmote: _ => { },
        Friends: new FriendsState(),
        AddFriend: _ => { },
        RemoveFriend: _ => { },
        ClearFriends: () => { },
        RequestLegacyFriends: () => { },
        Squelch: new SquelchState(),
        ModifyCharacterSquelch: (_, _, _, _) => { },
        ModifyAccountSquelch: (_, _) => { },
        ModifyGlobalSquelch: (_, _) => { },
        LastTeller: () => null,
        ClearDesiredComponents: () => { },
        HasOpenVendor: () => false,
        FillComponentBuyList: (_, _) => { },
        EnterPkLite: () => { },
        IsUsingTurbineChat: () => false,
        SetChatTitle: _ => { },
        SetSingleCharacterOption: (_, _) => { },
        AddPlayerPermission: _ => { },
        RemovePlayerPermission: _ => { },
        RequestAvailableHouses: _ => { },
        RequestChannelIndex: () => { },
        RequestChannelList: _ => { },
        JoinGmChannel: _ => { },
        LeaveGmChannel: _ => { },
        RecallAllegianceHometown: () => { },
        RequestAllegianceInfo: _ => { },
        AbandonHouse: () => { },
        Administration: NewAdministrationBindings(),
        IsPersistentDaylight: () => false,
        SetPersistentDaylight: _ => { },
        SetLandscapeRadius: _ => { },
        SetFieldOfView: _ => { });

    private static ClientCommandController.AdministrationBindings
        NewAdministrationBindings() => new(
            BreakAllegianceBoot: (_, _) => { },
            AllegianceChatBoot: (_, _) => { },
            AllegianceChatGag: (_, _) => { },
            AllegianceBroadcast: _ => { },
            ListAllegianceBans: () => { },
            AddAllegianceBan: _ => { },
            RemoveAllegianceBan: _ => { },
            ListAllegianceOfficers: () => { },
            ClearAllegianceOfficers: () => { },
            SetAllegianceOfficer: (_, _) => { },
            RemoveAllegianceOfficer: _ => { },
            ListAllegianceOfficerTitles: () => { },
            ClearAllegianceOfficerTitles: () => { },
            SetAllegianceOfficerTitle: (_, _) => { },
            QueryAllegianceName: () => { },
            SetAllegianceName: _ => { },
            ClearAllegianceName: () => { },
            AllegianceLockAction: _ => { },
            SetAllegianceApprovedVassal: _ => { },
            AllegianceHouseAction: _ => { },
            QueryMotd: () => { },
            SetMotd: _ => { },
            ClearMotd: () => { },
            SetOpenHouseStatus: _ => { },
            AddPermanentGuest: _ => { },
            RemovePermanentGuest: _ => { },
            RemoveAllPermanentGuests: () => { },
            ChangeStoragePermission: (_, _) => { },
            AddAllStoragePermission: () => { },
            RemoveAllStoragePermission: () => { },
            RequestFullGuestList: () => { },
            BootSpecificHouseGuest: _ => { },
            BootEveryone: () => { },
            SetHooksVisibility: _ => { },
            ModifyAllegianceGuestPermission: _ => { },
            ModifyAllegianceStoragePermission: _ => { });
}
