using System.Buffers.Binary;
using System.Net;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;

namespace AcDream.Runtime.Tests.Session;

public sealed class DirectGameRuntimeCommandAdapterTests
{
    [Fact]
    public void DirectRouteSendsTypedChatAndPortalAndRejectsOldGeneration()
    {
        var operations = new FixtureSessionOperations();
        var gameplay = new FixtureGameplayOperations();
        using var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay,
            SessionOperations: operations));
        gameplay.Bind(runtime);
        var resetHost = new FixtureResetHost();
        DirectGameRuntimeCommandAdapter? adapter = null;
        LiveSessionConnectOptions options = new(
            true,
            "127.0.0.1",
            9000,
            "account",
            "password");
        var live = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new FixtureEventRoute(),
                    session => adapter!.CreateRoute(session)),
                generation => runtime.ResetGeneration(
                    generation,
                    resetHost),
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            options);
        adapter = new DirectGameRuntimeCommandAdapter(runtime, live);
        var trace = new RuntimeTraceRecorder();
        using IDisposable subscription = runtime.Subscribe(trace);

        RuntimeSessionStartResult started =
            adapter.Session.Start(runtime.Generation);
        RuntimeGenerationToken firstGeneration = runtime.Generation;
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture =
            body => gameActions.Add(body);
        const uint selectedObject = 0x70000001u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = selectedObject,
            Type = ItemType.Misc,
        });

        RuntimeCommandResult chat = adapter.Chat.Execute(
            runtime.Generation,
            new RuntimeChatCommand(
                RuntimeChatChannel.Say,
                "hello"));
        RuntimeCommandResult portal = adapter.Portal.Execute(
            runtime.Generation,
            RuntimePortalCommand.RecallLifestone);
        runtime.CommunicationOwner.TurbineChat.OnChannelsReceived(
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
        RuntimeCommandResult[] stateAndWireCommands =
        [
            adapter.Selection.SelectObject(
                runtime.Generation,
                selectedObject),
            adapter.Selection.Clear(runtime.Generation),
            adapter.Movement.SetIntent(
                runtime.Generation,
                new MovementInput(Forward: true, Run: true)),
            adapter.Movement.ClearIntent(runtime.Generation),
            adapter.Movement.Execute(
                runtime.Generation,
                RuntimeMovementCommand.Stop),
            adapter.Chat.Execute(
                runtime.Generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.Fellowship,
                    "group")),
            adapter.Chat.Execute(
                runtime.Generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.General,
                    "global")),
            adapter.Chat.Execute(
                runtime.Generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.Roleplay,
                    "should be refused")),
            adapter.InventoryState.AddShortcut(
                runtime.Generation,
                new RuntimeShortcutCommand(0, 0x70000001u, 0u)),
            adapter.InventoryState.RemoveShortcut(
                runtime.Generation,
                0),
            adapter.Spellbook.AddFavorite(
                runtime.Generation,
                tabIndex: 0,
                position: 0,
                spellId: 7u),
            adapter.Spellbook.RemoveFavorite(
                runtime.Generation,
                tabIndex: 0,
                spellId: 7u),
            adapter.Spellbook.SetFilter(
                runtime.Generation,
                filters: 3u),
            adapter.Spellbook.ForgetSpell(
                runtime.Generation,
                spellId: 7u),
            adapter.Spellbook.SetDesiredComponent(
                runtime.Generation,
                componentId: 11u,
                amount: 3u),
            adapter.Spellbook.ClearDesiredComponents(
                runtime.Generation),
            adapter.Character.Advance(
                runtime.Generation,
                new RuntimeAdvancementCommand(
                    RuntimeAdvancementKind.Attribute,
                    StatId: 1u,
                    Cost: 10u)),
            adapter.Character.Advance(
                runtime.Generation,
                new RuntimeAdvancementCommand(
                    RuntimeAdvancementKind.Vital,
                    StatId: 2u,
                    Cost: 11u)),
            adapter.Character.Advance(
                runtime.Generation,
                new RuntimeAdvancementCommand(
                    RuntimeAdvancementKind.Skill,
                    StatId: 3u,
                    Cost: 12u)),
            adapter.Character.Advance(
                runtime.Generation,
                new RuntimeAdvancementCommand(
                    RuntimeAdvancementKind.TrainSkill,
                    StatId: 4u,
                    Cost: 1u)),
            adapter.Character.SetSingleOption(
                runtime.Generation,
                optionId: 0x26u,
                value: true),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeFriendCommand(
                    RuntimeFriendCommandKind.Add,
                    Name: "Friend")),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeFriendCommand(
                    RuntimeFriendCommandKind.Remove,
                    CharacterId: 0x50000003u)),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeFriendCommand(
                    RuntimeFriendCommandKind.Clear)),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeSquelchCommand(
                    RuntimeSquelchScope.Character,
                    Add: true,
                    CharacterId: 0x50000004u,
                    Name: "Muted")),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeSquelchCommand(
                    RuntimeSquelchScope.Account,
                    Add: true,
                    Name: "AccountMuted")),
            adapter.Social.Execute(
                runtime.Generation,
                new RuntimeSquelchCommand(
                    RuntimeSquelchScope.Global,
                    Add: true,
                    MessageType: 2u)),
        ];

        RuntimeSessionStartResult reconnected =
            adapter.Session.Reconnect(runtime.Generation);
        RuntimeCommandResult stale = adapter.Chat.Execute(
            firstGeneration,
            new RuntimeChatCommand(
                RuntimeChatChannel.Say,
                "stale"));
        RuntimeCommandResult staleMovement =
            adapter.Movement.SetIntent(
                firstGeneration,
                new MovementInput(Forward: true));

        Assert.Equal(RuntimeSessionStartStatus.Connected, started.Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            reconnected.Status);
        Assert.True(chat.Accepted);
        Assert.True(portal.Accepted);
        Assert.All(
            stateAndWireCommands,
            result => Assert.Equal(
                RuntimeCommandStatus.Accepted,
                result.Status));
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            stale.Status);
        Assert.Equal(
            RuntimeCommandStatus.StaleGeneration,
            staleMovement.Status);
        Assert.False(runtime.MovementOwner.HasCommandInput);
        Assert.True(gameActions.Count >= 20);
        Assert.Contains(
            runtime.CommunicationOwner.Chat.Snapshot(),
            entry => entry.Text.Contains(
                "not listening to the Roleplay channel",
                StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            trace.Entries,
            entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.Chat
                && entry.Text == "hello");
        Assert.Contains(
            trace.Entries,
            entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.Portal);
        Assert.Contains(
            trace.Entries,
            entry => entry.Kind == RuntimeTraceKind.Combat);

        RuntimeTeardownAcknowledgement stopped =
            adapter.Session.Stop(runtime.Generation);
        Assert.True(stopped.IsComplete);
        Assert.False(runtime.Session.IsInWorld);
    }


    [Fact]
    public void SetSingleOption_AutoSaveId_WritesLocalBitBeforeTheWireSendFires()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        uint? options2AtSendTime = null;
        operations.Sessions[^1].GameActionCapture = _ =>
            options2AtSendTime ??= runtime.CharacterOwner.Options.Options2;

        RuntimeCommandResult result = adapter.Character.SetSingleOption(
            runtime.Generation,
            (uint)CharacterOptionId.ListenToGeneralChat,
            false);

        Assert.True(result.Accepted);
        Assert.NotNull(options2AtSendTime);
        Assert.Equal(0u, options2AtSendTime!.Value & 0x00000100u);
        Assert.Equal(
            0u,
            runtime.CharacterOwner.Options.Options2 & 0x00000100u);
        runtime.Dispose();
    }

    [Fact]
    public void SetSingleOption_BatchedId_MarksDirtyWithoutSendingAnything()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        // AutoTarget (0x0D) is batched, default ON — flip it off.
        RuntimeCommandResult result = adapter.Character.SetSingleOption(
            runtime.Generation,
            (uint)CharacterOptionId.AutoTarget,
            false);

        Assert.True(result.Accepted);
        Assert.Empty(gameActions);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);
        runtime.Dispose();
    }

    [Fact]
    public void SetSingleOption_UnknownId_RejectsWithoutSendingAnything()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        RuntimeCommandResult result = adapter.Character.SetSingleOption(
            runtime.Generation,
            0x35u,
            true);

        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.Empty(gameActions);
        runtime.Dispose();
    }


    [Fact]
    public void SetTitle_SendsTheWireActionWithoutAnyLocalMutation()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        RuntimeCommandResult result = adapter.Character.SetTitle(
            runtime.Generation,
            titleId: 13u);

        Assert.True(result.Accepted);
        Assert.Single(gameActions);
        byte[] sent = gameActions[0];
        Assert.Equal(
            SocialActions.TitleSetOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(8)));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(12)));

        // CA-lesson: NO optimistic local mutation. RuntimeCharacterState.
        // Titles only updates from the server's own echo (0x002B/0x0029).
        Assert.Equal(0u, runtime.CharacterOwner.Titles.DisplayTitleId);
        Assert.Empty(runtime.CharacterOwner.Titles.EarnedTitleIds);
        runtime.Dispose();
    }

    [Fact]
    public void SetTitle_ZeroTitleId_StillSendsTheWireAction()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        RuntimeCommandResult result = adapter.Character.SetTitle(
            runtime.Generation,
            titleId: 0u);

        Assert.True(result.Accepted);
        Assert.Single(gameActions);
        byte[] sent = gameActions[0];
        Assert.Equal(
            SocialActions.TitleSetOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(sent.AsSpan(12)));
        runtime.Dispose();
    }

    [Fact]
    public void SetTitle_StaleGeneration_RejectsWithoutSendingAnything()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);
        RuntimeGenerationToken stale = runtime.Generation;
        _ = adapter.Session.Reconnect(runtime.Generation);

        RuntimeCommandResult result = adapter.Character.SetTitle(stale, titleId: 13u);

        Assert.Equal(RuntimeCommandStatus.StaleGeneration, result.Status);
        Assert.Empty(gameActions);
        runtime.Dispose();
    }

    [Fact]
    public void SaveOptions_FlushesTheDirtyBlobThenNoOpsWhenClean()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);
        SeedServerOptions(runtime);

        adapter.Character.SetSingleOption(
            runtime.Generation, (uint)CharacterOptionId.AutoTarget, false);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);
        Assert.Empty(gameActions);

        RuntimeCommandResult saved = adapter.Character.SaveOptions(runtime.Generation);

        Assert.True(saved.Accepted);
        Assert.False(runtime.CharacterOwner.Options.IsDirty);
        byte[] blob = Assert.Single(gameActions);
        Assert.Equal(
            SocialActions.SetCharacterOptionsOpcode,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                blob.AsSpan(8)));
        // S4 (OP1 review fix, blast lens): PrimaryObjectId no longer encodes
        // whether the flush actually fired — both hosts report the SAME
        // shape (Accepted, objectId 0).
        Assert.Equal(0u, saved.ResultObjectId);

        RuntimeCommandResult savedAgain =
            adapter.Character.SaveOptions(runtime.Generation);
        Assert.True(savedAgain.Accepted);
        Assert.Equal(0u, savedAgain.ResultObjectId);
        Assert.Single(gameActions);
        runtime.Dispose();
    }

    [Fact]
    public void SaveOptions_RefusesBeforeServerSeed_EvenWhenDirty_ThenSucceedsAfterSeed()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Character.SetSingleOption(
            runtime.Generation, (uint)CharacterOptionId.AutoTarget, false);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);

        RuntimeCommandResult saved = adapter.Character.SaveOptions(runtime.Generation);

        Assert.True(saved.Accepted);
        Assert.Empty(gameActions);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);

        SeedServerOptions(runtime);
        Assert.False(runtime.CharacterOwner.Options.IsDirty);
        adapter.Character.SetSingleOption(
            runtime.Generation, (uint)CharacterOptionId.ShowTooltips, false);

        RuntimeCommandResult savedAfterSeed =
            adapter.Character.SaveOptions(runtime.Generation);

        Assert.True(savedAfterSeed.Accepted);
        Assert.Single(gameActions);
        Assert.False(runtime.CharacterOwner.Options.IsDirty);
        runtime.Dispose();
    }


    [Fact]
    public void Session_Tick_AutoFlushesTheDirtyBlob_OnceThe480sTimerIsDue()
    {
        var clock = new ManualTimeProvider();
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness(clock);
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);
        SeedServerOptions(runtime);

        adapter.Character.SetSingleOption(
            runtime.Generation, (uint)CharacterOptionId.AutoTarget, false);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);

        // Before the timer: an ordinary tick must not flush.
        runtime.Session.Tick();
        Assert.Empty(gameActions);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);

        clock.Advance(RuntimeCharacterOptionsState.AutoSaveDelay + TimeSpan.FromSeconds(1));

        runtime.Session.Tick();

        byte[] blob = Assert.Single(gameActions);
        Assert.Equal(
            SocialActions.SetCharacterOptionsOpcode,
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                blob.AsSpan(8)));
        Assert.False(runtime.CharacterOwner.Options.IsDirty);
        runtime.Dispose();
    }

    [Fact]
    public void Stop_AutoFlushesTheDirtyBlob_BeforeTheCharacterLogoffRequest()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var order = new List<string>();
        operations.Sessions[^1].GameActionCapture = body =>
        {
            uint opcode = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                body.AsSpan(8));
            if (opcode == SocialActions.SetCharacterOptionsOpcode)
                order.Add("options-blob");
        };
        SeedServerOptions(runtime);
        adapter.Character.SetSingleOption(
            runtime.Generation, (uint)CharacterOptionId.AutoTarget, false);
        Assert.True(runtime.CharacterOwner.Options.IsDirty);

        RuntimeTeardownAcknowledgement stopped =
            adapter.Session.Stop(runtime.Generation);

        Assert.True(stopped.IsComplete);
        Assert.Equal(["options-blob"], order);
        runtime.Dispose();
    }


    private static uint ReadOpcode(byte[] gameAction) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
            gameAction.AsSpan(8));

    [Fact]
    public void Fellowship_Create_SendsTheCreateOpcodeWithNameAndShareXp()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        RuntimeCommandResult result = adapter.Fellowship.Create(
            runtime.Generation, "TestFellowship", shareXp: true);

        Assert.True(result.Accepted);
        byte[] sent = Assert.Single(gameActions);
        Assert.Equal(SocialActions.FellowshipCreateOpcode, ReadOpcode(sent));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_Create_RejectsAnEmptyName_WithoutSendingAnything()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        RuntimeCommandResult result = adapter.Fellowship.Create(
            runtime.Generation, string.Empty, shareXp: false);

        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.Empty(gameActions);
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_RecruitAndDismiss_SendTheirOpcodesWithTheTargetGuid()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Fellowship.Recruit(runtime.Generation, 0x50000005u);
        adapter.Fellowship.Dismiss(runtime.Generation, 0x50000006u);

        Assert.Equal(2, gameActions.Count);
        Assert.Equal(SocialActions.FellowshipRecruitOpcode, ReadOpcode(gameActions[0]));
        Assert.Equal(SocialActions.FellowshipDismissOpcode, ReadOpcode(gameActions[1]));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_Quit_NotLeader_SendsOnlyTheQuitOpcode()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        SeedFellowship(runtime, leader: 0x50000002u, self: 0x50000001u, others: 0x50000003u);
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Fellowship.Quit(runtime.Generation, disband: false);

        byte[] sent = Assert.Single(gameActions);
        Assert.Equal(SocialActions.FellowshipQuitOpcode, ReadOpcode(sent));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_Quit_LeaderWithoutDisbanding_SendsAssignNewLeaderBeforeQuit()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        SeedFellowship(runtime, leader: 0x50000001u, self: 0x50000001u, others: 0x50000003u);
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Fellowship.Quit(runtime.Generation, disband: false);

        Assert.Equal(2, gameActions.Count);
        Assert.Equal(SocialActions.FellowshipAssignNewLeaderOpcode, ReadOpcode(gameActions[0]));
        Assert.Equal(SocialActions.FellowshipQuitOpcode, ReadOpcode(gameActions[1]));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_Quit_LeaderDisbanding_SendsOnlyTheQuitOpcode()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        runtime.PlayerIdentity.ServerGuid = 0x50000001u;
        SeedFellowship(runtime, leader: 0x50000001u, self: 0x50000001u, others: 0x50000003u);
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Fellowship.Quit(runtime.Generation, disband: true);

        byte[] sent = Assert.Single(gameActions);
        Assert.Equal(SocialActions.FellowshipQuitOpcode, ReadOpcode(sent));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_AssignLeaderSetOpenSetPanelOpen_SendTheirOpcodes()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Fellowship.AssignLeader(runtime.Generation, 0x50000009u);
        adapter.Fellowship.SetOpen(runtime.Generation, isOpen: true);
        adapter.Fellowship.SetPanelOpen(runtime.Generation, panelOpen: true);

        Assert.Equal(3, gameActions.Count);
        Assert.Equal(SocialActions.FellowshipAssignNewLeaderOpcode, ReadOpcode(gameActions[0]));
        Assert.Equal(SocialActions.FellowshipChangeOpennessOpcode, ReadOpcode(gameActions[1]));
        Assert.Equal(SocialActions.FellowshipUpdateRequestOpcode, ReadOpcode(gameActions[2]));
        runtime.Dispose();
    }

    [Fact]
    public void Allegiance_SwearBreakKick_SendTheirOpcodesWithTheTargetGuid()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Allegiance.Swear(runtime.Generation, 0x50000010u);
        adapter.Allegiance.Break(runtime.Generation, 0x50000011u);
        adapter.Allegiance.Kick(runtime.Generation, 0x50000012u);

        Assert.Equal(3, gameActions.Count);
        Assert.Equal(AllegianceRequests.SwearOpcode, ReadOpcode(gameActions[0]));
        Assert.Equal(AllegianceRequests.BreakOpcode, ReadOpcode(gameActions[1]));
        Assert.Equal(AllegianceRequests.BreakOpcode, ReadOpcode(gameActions[2]));
        runtime.Dispose();
    }

    [Fact]
    public void Allegiance_RequestInfoAndSetUpdateSubscription_SendTheirOpcodes()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);

        adapter.Allegiance.RequestInfo(runtime.Generation, string.Empty);
        adapter.Allegiance.SetUpdateSubscription(runtime.Generation, on: true);

        Assert.Equal(2, gameActions.Count);
        Assert.Equal(ClientCommandRequests.AllegianceInfoRequestOpcode, ReadOpcode(gameActions[0]));
        Assert.Equal(AllegianceRequests.AllegianceUpdateRequestOpcode, ReadOpcode(gameActions[1]));
        runtime.Dispose();
    }

    [Fact]
    public void Fellowship_And_Allegiance_Commands_RejectAStaleGeneration()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, FixtureSessionOperations operations) =
            CreateStartedHarness();
        RuntimeGenerationToken stale = runtime.Generation;
        var gameActions = new List<byte[]>();
        operations.Sessions[^1].GameActionCapture = body => gameActions.Add(body);
        adapter.Session.Reconnect(runtime.Generation);

        RuntimeCommandResult fellowshipResult =
            adapter.Fellowship.Create(stale, "Stale", false);
        RuntimeCommandResult allegianceResult =
            adapter.Allegiance.Swear(stale, 0x50000001u);

        Assert.Equal(RuntimeCommandStatus.StaleGeneration, fellowshipResult.Status);
        Assert.Equal(RuntimeCommandStatus.StaleGeneration, allegianceResult.Status);
        Assert.Empty(gameActions);
        runtime.Dispose();
    }

    [Fact]
    public void TurnToHeading_RejectsAStaleGeneration()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, _) =
            CreateStartedHarness();
        RuntimeGenerationToken stale = runtime.Generation;
        adapter.Session.Reconnect(runtime.Generation);

        RuntimeCommandResult result =
            adapter.Movement.TurnToHeading(stale, 90f);

        Assert.Equal(RuntimeCommandStatus.StaleGeneration, result.Status);
        runtime.Dispose();
    }

    [Fact]
    public void TurnToHeading_IsRefusedBeforeTheWorldIsEntered()
    {
        (GameRuntime runtime, DirectGameRuntimeCommandAdapter adapter, _) =
            CreateStartedHarness();

        RuntimeCommandResult result =
            adapter.Movement.TurnToHeading(runtime.Generation, 90f);

        Assert.NotEqual(RuntimeCommandStatus.Accepted, result.Status);
        runtime.Dispose();
    }

    private static void SeedFellowship(
        GameRuntime runtime,
        uint leader,
        uint self,
        uint others)
    {
        GameEvents.FellowMember Member(uint guid, string name) => new(
            guid, 0u, 0u, 1u, 100u, 100u, 100u, 100u, 100u, 100u, 0u, name);
        runtime.FellowshipOwner.ApplyFullUpdate(new GameEvents.FellowshipFullUpdate(
            [Member(leader, "Leader"), Member(self, "Self"), Member(others, "Other")],
            "The Fellows",
            leader,
            ShareXp: true,
            EvenXpSplit: false,
            OpenFellow: true,
            Locked: false,
            Departed: []));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private static void SeedServerOptions(GameRuntime runtime) =>
        runtime.CharacterOwner.Options.Replace(
            runtime.CharacterOwner.Options.Options1,
            runtime.CharacterOwner.Options.Options2);

    private static (GameRuntime Runtime, DirectGameRuntimeCommandAdapter Adapter, FixtureSessionOperations Operations)
        CreateStartedHarness(TimeProvider? timeProvider = null)
    {
        var operations = new FixtureSessionOperations();
        var gameplay = new FixtureGameplayOperations();
        var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay,
            TimeProvider: timeProvider,
            SessionOperations: operations));
        gameplay.Bind(runtime);
        var resetHost = new FixtureResetHost();
        DirectGameRuntimeCommandAdapter? adapter = null;
        LiveSessionConnectOptions options = new(
            true,
            "127.0.0.1",
            9000,
            "account",
            "password");
        var live = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new FixtureEventRoute(),
                    session => adapter!.CreateRoute(session)),
                generation => runtime.ResetGeneration(
                    generation,
                    resetHost),
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    runtime.CommunicationOwner.Chat.SetLocalPlayerGuid,
                    _ => { },
                    _ => { },
                    runtime.ActionOwner.Combat.Clear),
                new LiveSessionEnteredWorldBindings(
                    _ => { },
                    () => { },
                    () => { },
                    _ => { },
                    () => { }),
                (_, _, _) => { },
                () => { },
                _ => { },
                _ => { }),
            options);
        adapter = new DirectGameRuntimeCommandAdapter(runtime, live);
        _ = adapter.Session.Start(runtime.Generation);
        return (runtime, adapter, operations);
    }

    private sealed class FixtureSessionOperations : ILiveSessionOperations
    {
        public List<WorldSession> Sessions { get; } = [];

        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(
                endpoint,
                new FixtureTransport());
            Sessions.Add(session);
            return session;
        }

        public void Connect(
            WorldSession session,
            string user,
            string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(
                        0x50000001u,
                        "Direct",
                        0u),
                ],
                [],
                11,
                "account",
                true,
                true);

        public void EnterWorld(
            WorldSession session,
            int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) =>
            session.Dispose();
    }

    private sealed class FixtureEventRoute : ILiveSessionEventRouting
    {
        public void Attach()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FixtureResetHost : IRuntimeGenerationResetHost
    {
        public void RetireEntityProjection(RuntimeEntityRecord entity)
        {
        }

        public void DrainEntityProjectionBoundary()
        {
        }

        public void CompleteEntityProjectionRetirement()
        {
        }
    }

    private sealed class FixtureTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram)
        {
        }

        public void Send(
            IPEndPoint remote,
            ReadOnlySpan<byte> datagram)
        {
        }

        public int Receive(
            Span<byte> destination,
            TimeSpan timeout,
            out IPEndPoint? from)
        {
            from = null;
            return -1;
        }

        public ValueTask<NetReceiveResult> ReceiveAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<NetReceiveResult>(
                new OperationCanceledException(cancellationToken));

        public void Dispose()
        {
        }
    }

    private sealed class FixtureGameplayOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        private GameRuntime? _runtime;

        public void Bind(GameRuntime runtime) => _runtime = runtime;
        public bool CanStartAttack() => false;
        public void PrepareAttackRequest()
        {
        }

        public bool SendAttack(AttackHeight height, float power) => false;
        public void SendCancelAttack()
        {
        }

        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => _runtime?.Session.IsInWorld == true;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest()
        {
        }

        public void SendChangeCombatMode(CombatMode mode)
        {
        }

        public uint LocalPlayerId =>
            _runtime?.PlayerIdentity.ServerGuid ?? 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;

        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => false;

        public void StopCompletely()
        {
        }

        public void SendUntargeted(uint spellId)
        {
        }

        public void SendTargeted(uint targetId, uint spellId)
        {
        }

        public void DisplayMessage(string message)
        {
        }

        public void IncrementBusy()
        {
        }
    }
}
