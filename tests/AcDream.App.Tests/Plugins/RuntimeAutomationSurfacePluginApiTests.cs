using AcDream.App.Interaction;
using AcDream.App.Plugins;
using AcDream.Runtime.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Plugins;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Items;
using AcDream.Runtime.Session;
using System.Net;

namespace AcDream.App.Tests.Plugins;

public sealed class RuntimeAutomationSurfacePluginApiTests
{
    /// <summary>
    /// Mutation: omit ObjectClass or clamp zero in BuildOwnedEquipment;
    /// the plugin host then loses weapon-class or empty-stack information.
    /// </summary>
    [Fact]
    public void GraphicalHostEquipmentProjectsClassAndExplicitZeroStack()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        const uint itemId = 0x700000ABu;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Empty bow stack",
            Type = ItemType.MissileWeapon,
            ValidLocations = EquipMask.Held,
            ContainerId = 0x50000001u,
            StackSize = 0,
        });
        IPluginHost host = new AppPluginHost(
            new TestPluginLogger(), new WorldGameState(), new WorldEvents(),
            new SelectionState(), NoOpUiRegistry.Instance, surface);

        PluginEquipmentItem item = Assert.Single(
            host.Automation.Equipment.CaptureOwnedEquipment(),
            item => item.ObjectId == itemId);
        Assert.Equal(PluginObjectClass.MissileWeapon, item.ObjectClass);
        Assert.Equal(0, item.StackSize);
    }

    [Fact]
    public void MapWorldObjectUseOutcomeMapsEveryOutcomeToItsPluginStatus()
    {
        Assert.Equal(
            PluginItemCommandStatus.Started,
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.Started).Status);
        Assert.Equal(
            PluginItemCommandStatus.Busy,
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.Busy).Status);
        Assert.Equal(
            PluginItemCommandStatus.Refused,
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.NotUseable).Status);
        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.NotInWorld).Status);
        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            RuntimeAutomationSurface.MapWorldObjectUseOutcome(
                AutomationUseOutcome.Unavailable).Status);
    }

    [Fact]
    public void MapWorldObjectUseOutcomeGivesNotUseableAHumanNotice()
    {
        PluginItemCommandResult result = RuntimeAutomationSurface.MapWorldObjectUseOutcome(
            AutomationUseOutcome.NotUseable);

        Assert.Equal("That cannot be used.", result.Notice);
    }

    /// <summary>
    /// The casting flag means a cast this session issued is still
    /// outstanding. It once answered from the inventory transaction count,
    /// which an appraisal or a pickup raises as readily as a cast -- so an
    /// automation that appraises as it walks read as permanently mid-cast,
    /// its cast gate answered Busy for ever, and every rule that casts was
    /// refused on every pass. Mutation: answer from
    /// <c>Transactions.BusyCount &gt; 0</c> again and the appraisal reads as
    /// a cast.
    /// </summary>
    [Fact]
    public void IsCastingIsACastInFlightAndNotAPendingInventoryRequest()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);

        Assert.False(surface.Magic.IsCasting);

        // An appraisal, a pickup or any other request in flight: the count
        // is up, but no spell is on its way.
        runtime.InventoryOwner.Transactions.IncrementBusyCount();

        Assert.False(surface.Magic.IsCasting);
    }

    [Fact]
    public void UsingAWorldObjectThePluginDoesNotOwnRoutesThroughTheWalkToUsePath()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        surface.BindItems(
            useItem: _ => false,
            applyItem: (_, _) => false,
            moveItem: (_, _, _, _) => false,
            mergeItems: (_, _, _) => false,
            dropItem: (_, _) => false,
            giveItem: (_, _, _) => false,
            pickupItem: (_, _) => AcDream.Runtime.Gameplay
                .RuntimeBackpackPlacementOutcome.NotThisClients,
            identifyItem: _ => false);

        // A landscape vendor at distance -- not in the player's
        // inventory, wielded slots, or backpack chain.
        const uint vendorId = 0x8000_0001u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = vendorId,
            ContainerId = 0u,
        });

        uint? walkedTo = null;
        surface.BindWorldObjectUse(id =>
        {
            walkedTo = id;
            return new PluginItemCommandResult(PluginItemCommandStatus.Started);
        });

        PluginItemCommandResult result = surface.Items.Use(vendorId);

        Assert.Equal(vendorId, walkedTo);
        Assert.Equal(PluginItemCommandStatus.Started, result.Status);
    }

    [Fact]
    public void UsingAnOwnedTargetedUseItemGetsASpecificNoticeInsteadOfABareRefusal()
    {
        // A Mana Stone (and any other item that can
        // only recharge/act on ANOTHER item) has a targeted Useability --
        // Use(objectId) alone can never complete it, since there is no
        // target to carry. TryUseItemForAutomation already refused it for
        // exactly that reason, but the refusal came back bare (Refused,
        // no notice), indistinguishable from a busy gate or a genuinely
        // broken item. DispatchItem must name the real reason and tell
        // the caller what to do instead.
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        surface.BindItems(
            useItem: _ => true,
            applyItem: (_, _) => true,
            moveItem: (_, _, _, _) => false,
            mergeItems: (_, _, _) => false,
            dropItem: (_, _) => false,
            giveItem: (_, _, _) => false,
            pickupItem: (_, _) => AcDream.Runtime.Gameplay
                .RuntimeBackpackPlacementOutcome.NotThisClients,
            identifyItem: _ => false);

        uint playerId = runtime.PlayerIdentity.ServerGuid;
        const uint manaStoneId = 0x8000_1234u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = manaStoneId,
            ContainerId = playerId,
            Useability = ItemUseability.Contained << 16,
        });

        PluginItemCommandResult result = surface.Items.Use(manaStoneId);

        Assert.Equal(PluginItemCommandStatus.Refused, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
        Assert.Contains("target", result.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UsingAWorldObjectWithoutAWalkToUseRouteBoundIsRefusedRatherThanSilentlyIgnored()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        surface.BindItems(
            useItem: _ => false,
            applyItem: (_, _) => false,
            moveItem: (_, _, _, _) => false,
            mergeItems: (_, _, _) => false,
            dropItem: (_, _) => false,
            giveItem: (_, _, _) => false,
            pickupItem: (_, _) => AcDream.Runtime.Gameplay
                .RuntimeBackpackPlacementOutcome.NotThisClients,
            identifyItem: _ => false);

        const uint vendorId = 0x8000_0002u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = vendorId,
            ContainerId = 0u,
        });

        PluginItemCommandResult result = surface.Items.Use(vendorId);

        Assert.Equal(PluginItemCommandStatus.InvalidItem, result.Status);
    }

    [Fact]
    public void ChatReceivedFiresInArrivalOrderWithTheTextClassAndTime()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginChatMessage>();
        surface.Chat.Received += seen.Add;
        DateTimeOffset before = DateTimeOffset.UtcNow.AddSeconds(-1);

        runtime.CommunicationOwner.Chat.OnSystemMessage("first", 0x0Du);
        runtime.CommunicationOwner.Chat.OnCombatLine(
            "second",
            logTextType: 0x0Eu,
            kind: CombatLineKind.Warning);

        Assert.Equal(["first", "second"], seen.Select(static m => m.Text));
        Assert.True(seen[1].Sequence > seen[0].Sequence);
        Assert.Equal(0x0D, seen[0].LogTextType);
        Assert.Equal(0x0E, seen[1].LogTextType);
        Assert.Equal(0, seen[0].CombatKind);
        Assert.Equal(2, seen[1].CombatKind);
        Assert.True(seen[0].Received >= before);
    }

    [Fact]
    public void AFilteredLineReachesNeitherTheTranscriptNorTheEventNorTheRing()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<string>();
        surface.Chat.Received += message => seen.Add(message.Text);
        using IDisposable filter = surface.Chat.RegisterFilter(
            static candidate => candidate.Text.Contains(
                "drop me",
                StringComparison.Ordinal));

        runtime.CommunicationOwner.Chat.OnSystemMessage("please drop me", 0u);
        runtime.CommunicationOwner.Chat.OnSystemMessage("keep me", 0u);

        Assert.Equal(["keep me"], seen);
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("keep me", entry.Text);
        PluginChatMessage captured = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal("keep me", captured.Text);
    }

    [Fact]
    public void AFilterInstalledBeforeLoginStillAppliesToTheNextSession()
    {
        using var surface = new RuntimeAutomationSurface();
        using IDisposable filter = surface.Chat.RegisterFilter(static _ => true);

        using var runtime = GameRuntimeTestFactory.Create();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CommunicationOwner.Chat.OnSystemMessage("dropped", 0u);

        Assert.Equal(0, runtime.CommunicationOwner.Chat.Count);
    }

    /// <summary>
    /// The windowed client's plugin host over the shared surface, and the
    /// bus its session composition hangs off that surface: an interceptor a
    /// plugin registers through the host is what the bus answers the chat
    /// router with, so a typed line goes out rewritten.
    /// </summary>
    [Fact]
    public void AnInterceptorRegisteredThroughTheWindowedHostRewritesWhatTheRouterSends()
    {
        using var surface = new RuntimeAutomationSurface();
        IPluginHost host = new AppPluginHost(
            new TestPluginLogger(), new WorldGameState(), new WorldEvents(),
            new SelectionState(), NoOpUiRegistry.Instance, surface);
        using IDisposable alias = host.Automation.Chat.RegisterInputInterceptor(
            static typed => typed == "go"
                ? PluginChatInputDecision.Rewrite("hello there")
                : PluginChatInputDecision.Pass);
        // The bus exactly as the session composition builds it, with the
        // surface's decision behind it.
        var bus = new AcDream.App.Net.LiveSessionCommandSurface(
            interceptChatInput: surface.InterceptChatInput);
        using var communication = new RuntimeCommunicationState();

        PluginChatInputDecision decision =
            ((AcDream.Runtime.Chat.IPluginCommandBus)bus).InterceptChatInput("go");
        var recording = new RecordingPluginBus(bus);
        AcDream.Runtime.Chat.SubmitOutcome outcome = AcDream.Runtime.Chat.ChatCommandRouter.Submit(
            "go",
            new AcDream.Runtime.Chat.RuntimeChatCommandFeedback(communication),
            recording,
            AcDream.Runtime.Chat.ChatChannelKind.Say);

        Assert.Equal(PluginChatInputAction.Rewrite, decision.Action);
        Assert.Equal("hello there", decision.Text);
        Assert.Equal(AcDream.Runtime.Chat.SubmitOutcome.Sent, outcome);
        var said = Assert.IsType<AcDream.Runtime.Chat.SendChatCmd>(
            Assert.Single(recording.Published));
        Assert.Equal("hello there", said.Text);
    }

    /// <summary>
    /// The windowed bus with what was published kept, so the words that
    /// would have gone to the world can be read back.
    /// </summary>
    private sealed class RecordingPluginBus(
        AcDream.Runtime.Chat.IPluginCommandBus inner)
        : AcDream.Runtime.Chat.IPluginCommandBus
    {
        internal List<object> Published { get; } = [];

        public void Publish<T>(T command) where T : notnull =>
            Published.Add(command);

        public bool TryHandlePluginCommand(string commandLine) =>
            inner.TryHandlePluginCommand(commandLine);

        public PluginChatInputDecision InterceptChatInput(string typed) =>
            inner.InterceptChatInput(typed);
    }

    [Fact]
    public void UnbindingRemovesTheSurfaceFiltersFromTheSessionsLog()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        using IDisposable filter = surface.Chat.RegisterFilter(static _ => true);

        surface.Unbind();
        runtime.CommunicationOwner.Chat.OnSystemMessage("kept", 0u);

        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
    }

    [Fact]
    public void PostMessageUsesTheRequestedTextClass()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage("A tinted line.", (int)RetailLogTextType.Magic);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("A tinted line.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Magic, entry.LogTextType);
    }

    [Fact]
    public void PostMessageRejectsTheStatusOnlyClientLocalClassAndFallsBackToDefault()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage(
            "should not become a status notice",
            (int)RetailLogTextType.ClientLocal);

        // ClientLocal routes to the status overlay, not the transcript, and
        // is re-offered to status filters. A plugin has no legitimate reason
        // to post there, so the surface falls back to the default class.
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("should not become a status notice", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
        runtime.CommunicationOwner.SpewBox.Tick(0);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void PostMessageRejectsAnOutOfRangeTextClassAndFallsBackToDefault()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostMessage("out of range", -1);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void PostSystemMessageThrowsOnNullText()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Throws<ArgumentNullException>(() => surface.Chat.PostSystemMessage(null!));
    }

    [Fact]
    public void PostSystemMessageIgnoresAnEmptyString()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.Chat.PostSystemMessage(string.Empty);

        Assert.Empty(runtime.CommunicationOwner.Chat.Snapshot());
    }

    [Fact]
    public void LoginCompleteFiresOnEachInWorldEdgeAndLogoffOnEachExit()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        int logoffs = 0;
        events.LoginComplete += () => logins++;
        events.Logoff += () => logoffs++;

        Enter(runtime, commands);
        Assert.Equal(1, logins);

        Leave(runtime, commands);
        Assert.Equal(1, logoffs);

        // A reconnect keeps the same plugin instance, so the edge must fire
        // again rather than only once per process.
        Enter(runtime, commands);
        Assert.Equal(2, logins);
        Assert.Equal(1, logoffs);
    }

    /// <summary>
    /// Logoff reaches plugins before the session's state is reset, as the
    /// contract says, and only once: the reset that follows releases every
    /// object, and a plugin tracking inventory must read that after it was
    /// told the session is ending, not as the player dropping everything.
    /// The handler can still read the game. Mutation: drop the controller's
    /// leaving-world announcement and the order flips to reset-then-logoff,
    /// with the surface already unavailable.
    /// </summary>
    [Fact]
    public void LogoffReachesPluginsBeforeTheSessionIsResetAndOnlyOnce()
    {
        var order = new List<string>();
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession(reset: _ => order.Add("reset"));
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        bool readableAtLogoff = false;
        events.Logoff += () =>
        {
            order.Add("logoff");
            readableAtLogoff = surface.IsAvailable;
        };

        Enter(runtime, commands);
        order.Clear();
        Leave(runtime, commands);

        Assert.Equal(["logoff", "reset"], order);
        Assert.True(readableAtLogoff);
    }

    [Fact]
    public void LogoffToCharacterSelectReachesPluginsBeforeTheResetAndOnlyOnce()
    {
        var order = new List<string>();
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession(reset: _ => order.Add("reset"));
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var observer = new LeavingObserver();
        using var subscription = runtime.Subscribe(observer);
        events.Logoff += () => order.Add("logoff");
        events.LoginComplete += () => order.Add("login");

        Enter(runtime, commands);
        order.Clear();
        observer.Clear();
        Assert.True(runtime.Session.CompleteCharacterLogOff(runtime.Generation).Accepted);
        runtime.SyncLifecycleEmission();
        runtime.SyncLifecycleEmission();

        Assert.Equal(["logoff", "reset"], order);
        Assert.Equal(["leaving", "InWorld->Starting"], observer.Log);
        Assert.False(surface.IsAvailable);
    }

    [Fact]
    public void ALogoffStraightIntoTheNextCharacterRaisesLogoffThenLoginComplete()
    {
        var order = new List<string>();
        var events = new WorldEvents();
        var operations = new RealSessionOperations();
        operations.Roster.Add(new CharacterList.Character(0x50000002u, "PluginApiSecond", 0u));
        var (runtime, commands) = CreateRealSession(operations: operations);
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var observer = new LeavingObserver();
        using var subscription = runtime.Subscribe(observer);
        events.Logoff += () => order.Add("logoff");
        events.LoginComplete += () => order.Add("login " + surface.Name);

        Enter(runtime, commands);
        order.Clear();
        observer.Clear();
        Assert.True(runtime.Session.TrySetNextLogin(0x50000002u));
        Assert.True(runtime.Session.CompleteCharacterLogOff(runtime.Generation).Accepted);
        Assert.True(runtime.Session.IsInWorld);
        runtime.SyncLifecycleEmission();
        runtime.SyncLifecycleEmission();

        Assert.Equal(["logoff", "login PluginApiSecond"], order);
        Assert.Equal(
            ["leaving", "InWorld->Starting", "Starting->InWorld"],
            observer.Log);
        Assert.True(surface.IsAvailable);
    }

    [Fact]
    public void ALostConnectionReachesPluginsAsOneLogoffBeforeTheReset()
    {
        var order = new List<string>();
        var events = new WorldEvents();
        var operations = new RealSessionOperations();
        var (runtime, commands) = CreateRealSession(
            reset: _ => order.Add("reset"),
            operations: operations);
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var observer = new LeavingObserver();
        using var subscription = runtime.Subscribe(observer);
        events.Logoff += () => order.Add("logoff");

        Enter(runtime, commands);
        order.Clear();
        observer.Clear();
        operations.Lost = true;
        runtime.Session.Tick();
        runtime.SyncLifecycleEmission();

        Assert.Equal(["logoff", "reset"], order);
        Assert.Equal(["leaving", "InWorld->Stopped"], observer.Log);
    }

    [Fact]
    public void AStopFromInsideTheLogoffHandlerAnnouncesTheLeaveOnce()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var observer = new LeavingObserver();
        using var subscription = runtime.Subscribe(observer);
        int logoffs = 0;
        events.Logoff += () =>
        {
            logoffs++;
            commands.Stop(runtime.Generation);
        };

        Enter(runtime, commands);
        observer.Clear();
        Leave(runtime, commands);
        runtime.Session.Tick();
        runtime.SyncLifecycleEmission();

        Assert.Equal(1, logoffs);
        Assert.Equal(1, observer.Log.Count(entry => entry == "leaving"));
        Assert.False(runtime.Session.IsInWorld);
    }

    [Fact]
    public void WhatACharacterAnnouncedIsNotReadBackAfterItLeaves()
    {
        string peers = Path.Combine(
            Path.GetTempPath(),
            "acdream-own-announcements-" + Guid.NewGuid().ToString("N"));
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        try
        {
            using var surface = new RuntimeAutomationSurface(
                events,
                new LocalPluginPeerRegistry(peers));
            surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
            INetworkAutomation network = surface.Network;

            Enter(runtime, commands);
            Assert.True(network.BroadcastCommand("/go", [], 0));
            Assert.Single(network.CaptureOwnCommands(0L));
            Assert.True(runtime.Session.CompleteCharacterLogOff(runtime.Generation).Accepted);
            runtime.SyncLifecycleEmission();

            Assert.Empty(network.CaptureOwnCommands(0L));
            Assert.Empty(network.CaptureOwnCasts(0L));
        }
        finally
        {
            if (Directory.Exists(peers))
                Directory.Delete(peers, recursive: true);
        }
    }

    [Fact]
    public void ASessionThatNeverReachedTheWorldAnnouncesNoLeave()
    {
        var events = new WorldEvents();
        var operations = new RealSessionOperations();
        operations.Roster.Clear();
        var (runtime, commands) = CreateRealSession(operations: operations);
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var observer = new LeavingObserver();
        using var subscription = runtime.Subscribe(observer);
        int logoffs = 0;
        events.Logoff += () => logoffs++;

        Enter(runtime, commands);
        Assert.False(runtime.Session.IsInWorld);
        Leave(runtime, commands);

        Assert.Equal(0, logoffs);
        Assert.DoesNotContain("leaving", observer.Log);
    }

    [Fact]
    public void CharacterIdentityIsPopulatedTheMomentLoginCompleteFires()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        string nameSeenInHandler = string.Empty;
        string accountSeenInHandler = string.Empty;
        uint objectIdSeenInHandler = 0u;
        int characterIndexSeenInHandler = -1;
        events.LoginComplete += () =>
        {
            nameSeenInHandler = surface.Name;
            accountSeenInHandler = surface.AccountName;
            objectIdSeenInHandler = surface.ObjectId;
            characterIndexSeenInHandler = surface.CharacterIndex;
        };

        Enter(runtime, commands);

        Assert.Equal("PluginApiFixture", nameSeenInHandler);
        Assert.Equal("PluginApi", accountSeenInHandler);
        Assert.Equal(0x50000001u, objectIdSeenInHandler);
        Assert.Equal(0, characterIndexSeenInHandler);
    }

    [Fact]
    public void RepeatedInWorldReportsDoNotFireLoginCompleteTwice()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        events.LoginComplete += () => logins++;

        Enter(runtime, commands);
        Enter(runtime, commands);

        Assert.Equal(1, logins);
    }

    [Fact]
    public void TheDeathMessageReachesPluginsWithoutMatchingChatText()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var deaths = new List<string>();
        events.LocalPlayerDied += deaths.Add;

        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(["You have died!"], deaths);
    }

    [Fact]
    public void UnbindingStopsDeathAndLifecycleReports()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int logins = 0;
        var deaths = new List<string>();
        events.LoginComplete += () => logins++;
        events.LocalPlayerDied += deaths.Add;

        surface.Unbind();
        Enter(runtime, commands);
        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(0, logins);
        Assert.Empty(deaths);
    }

    [Fact]
    public void TheSpellCatalogExposesTheWholeTableAndFindsSpellsByName()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.CharacterOwner.Spellbook.InstallMetadata(SpellTable.Create(
        [
            Spell(1u, "Strength Self VI"),
            Spell(2u, "Heal Self VII"),
        ]));

        // Nothing is learned, so the known lists stay empty while the table
        // is fully visible.
        Assert.Empty(surface.Spells.KnownSelfBuffs);
        Assert.Equal(2, surface.Spells.All.Count);
        Assert.Same(surface.Spells.All, surface.Spells.All);

        Assert.True(surface.Spells.TryFindByName(
            "heal self vii",
            partialMatch: false,
            out PluginSpellInfo exact));
        Assert.Equal(2u, exact.SpellId);

        Assert.False(surface.Spells.TryFindByName(
            "Heal Self",
            partialMatch: false,
            out _));
        Assert.True(surface.Spells.TryFindByName(
            "Heal Self",
            partialMatch: true,
            out PluginSpellInfo partial));
        Assert.Equal(2u, partial.SpellId);

        Assert.False(surface.Spells.TryFindByName(
            "Nonexistent",
            partialMatch: true,
            out _));
    }

    [Fact]
    public void ServerPopulationIsUnknownUntilTheServerReportsIt()
    {
        using var surface = new RuntimeAutomationSurface();
        Assert.Equal(-1, surface.Character.ServerPopulation);
    }

    [Fact]
    public void ServerPopulationReachesTheSurfaceFromTheLoginTimeWorldNameMessage()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        runtime.Session.CharacterSelectionState.ApplyWorldName(
            "Thistledown",
            serverPopulation: 274);

        Assert.Equal(274, surface.Character.ServerPopulation);
    }

    [Fact]
    public void ObjectChangedMapsEntityAndInventoryDeltasToPluginKinds()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;
        var observer = (IRuntimeEventObserver)surface;
        RuntimeEventStamp stamp = default;

        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Registered,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Rebucketed,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Withdrawn,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnEntity(new RuntimeEntityDelta(
            stamp,
            RuntimeEntityChange.Hidden,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Added,
            new RuntimeInventoryItemSnapshot(
                200u, 1, "Item", 0u, 0, 0u, 0u, 0, 0)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Removed,
            new RuntimeInventoryItemSnapshot(
                200u, 1, "Item", 0u, 0, 0u, 0u, 0, 0)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Cleared,
            default));

        Assert.Equal(
            new (uint ObjectId, PluginObjectChangeKind Kind)[]
            {
                (100u, PluginObjectChangeKind.Created),
                (100u, PluginObjectChangeKind.Moved),
                (100u, PluginObjectChangeKind.Released),
                (100u, PluginObjectChangeKind.Updated),
                (200u, PluginObjectChangeKind.Created),
                (200u, PluginObjectChangeKind.Released),
            },
            seen.Select(static c => (c.ObjectId, c.Kind)));
    }

    [Fact]
    public void ContainerOpenedAndClosedFireOnExternalContainerTransitions()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        uint? opened = null;
        uint? closed = null;
        events.ContainerOpened += id => opened = id;
        events.ContainerClosed += id => closed = id;

        runtime.InventoryOwner.ExternalContainers.RequestOpen(500u);
        runtime.InventoryOwner.ExternalContainers.ApplyViewContents(500u);
        Assert.Equal(500u, opened);
        Assert.Null(closed);

        runtime.InventoryOwner.ExternalContainers.ApplyClose(500u);
        Assert.Equal(500u, closed);
    }

    [Fact]
    public void ObjectChangedReportsIdentReceivedOnBothTheFirstResponseAndARefresh()
    {
        // AcceptAppraisalResponse now raises AppraisalReceived (and so this
        // ObjectChanged) on every accepted response, not just the first --
        // a refresh of already-held data can still carry a changed payload
        // (durability, stack count) that observers need to see.
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;

        runtime.ActionOwner.Transactions.TryRequestAppraisal(
            700u, static _ => { });
        runtime.ActionOwner.Transactions.AcceptAppraisalResponse(700u);
        runtime.ActionOwner.Transactions.AcceptAppraisalResponse(700u);

        Assert.Equal(
            2,
            seen.Count(c =>
                c.ObjectId == 700u
                    && c.Kind == PluginObjectChangeKind.IdentReceived));
    }

    [Fact]
    public void LootAppraisalCurrentObjectIdAdvancesForAnAutomationResponseThatDoesNotPresent()
    {
        // Pins RuntimeAutomationSurface's ILootAutomation.Appraisal mapping:
        // PluginAppraisalState.CurrentObjectId must read
        // RuntimeInteractionTransactionState.LastCompletedAppraisalId, not
        // CurrentAppraisalId (the examination window's presentation
        // target). An Automation-origin response for an object other than
        // whatever the window is showing never retargets presentation, so
        // if this mapping regressed back to CurrentAppraisalId, a plugin
        // polling Appraisal.CurrentObjectId for its own Identify to finish
        // would never see it -- exactly the corpse-looting stall HIGH-1
        // fixed.
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        ILootAutomation loot = surface.Loot;

        Assert.Equal(0u, loot.Appraisal.CurrentObjectId);

        runtime.ActionOwner.Transactions.TryRequestAppraisal(
            701u,
            static _ => { },
            AppraisalRequestOrigin.Automation);
        RuntimeAppraisalResponseAcceptance acceptance =
            runtime.ActionOwner.Transactions.AcceptAppraisalResponse(701u);

        // The response did not retarget presentation (nothing was already
        // current for it to refresh in place) -- proving this test would
        // have caught a regression back to mapping CurrentAppraisalId.
        Assert.False(acceptance.PresentInUi);
        Assert.Equal(0u, runtime.ActionOwner.Transactions.CurrentAppraisalId);

        Assert.Equal(701u, loot.Appraisal.CurrentObjectId);
        Assert.Equal(0u, loot.Appraisal.AwaitingObjectId);
    }

    /// <summary>
    /// A description in flight must not make the surface refuse the next
    /// item action. A looter describes what it is about to take, and while
    /// the two shared one count its own next Open or Pickup came back Busy,
    /// so it retried the corpse it could have opened at once. Mutation:
    /// count an appraisal on the item-action count again and the loot
    /// surface reports itself busy.
    /// </summary>
    [Fact]
    public void AnOutstandingIdentifyDoesNotMakeTheItemSurfaceBusy()
    {
        var events = new WorldEvents();
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(runtime.ActionOwner.Transactions.TryRequestAppraisal(
            702u,
            static _ => { },
            AppraisalRequestOrigin.Automation));

        Assert.False(surface.Loot.IsBusy);
        Assert.False(surface.Items.IsBusy);
        Assert.True(runtime.InventoryOwner.Transactions.CanBeginRequest);

        // And one description at a time is still the rule.
        Assert.False(runtime.InventoryOwner.Transactions.CanBeginAppraisal);
    }

    [Fact]
    public void WorldObjectIdentifyAcceptsAnOwnedInventoryItemAndReportsIdentReceived()
    {
        var events = new WorldEvents();
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);

        uint? sentTo = null;
        surface.BindItems(
            useItem: _ => false,
            applyItem: (_, _) => false,
            moveItem: (_, _, _, _) => false,
            mergeItems: (_, _, _) => false,
            dropItem: (_, _) => false,
            giveItem: (_, _, _) => false,
            pickupItem: (_, _) => AcDream.Runtime.Gameplay
                .RuntimeBackpackPlacementOutcome.NotThisClients,
            identifyItem: id => runtime.ActionOwner.Transactions.TryRequestAppraisal(
                id,
                sent => sentTo = sent));

        // An owned inventory item -- not a corpse, not inside the
        // currently open container -- which ILootAutomation.Identify's
        // container-scoped rule would refuse as InvalidItem.
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 900u,
            ContainerId = 1u,
        });

        var seen = new List<PluginObjectChange>();
        events.ObjectChanged += seen.Add;

        PluginItemCommandResult result = surface.Objects.Identify(900u);

        Assert.Equal(PluginItemCommandStatus.Started, result.Status);
        Assert.Equal(900u, sentTo);

        runtime.ActionOwner.Transactions.AcceptAppraisalResponse(900u);

        Assert.Contains(
            seen,
            c => c.ObjectId == 900u && c.Kind == PluginObjectChangeKind.IdentReceived);
    }

    [Fact]
    public void TryCaptureProperties_returnsTheRetainedWeaponAndArmorProfiles()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        var weapon = new ClientWeaponProfile(
            DamageType: 4u,
            WeaponTime: 30u,
            WeaponSkill: 34u,
            Damage: 12u,
            DamageVariance: 0.2d,
            DamageMod: 1.1d,
            WeaponLength: 1.0d,
            MaxVelocity: 2.0d,
            WeaponOffense: 1.05d,
            MaxVelocityEstimated: 1u);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 901u,
            WeaponProfile = weapon,
        });

        bool ok = surface.Objects.TryCaptureProperties(
            901u, out PluginItemProperties properties);

        Assert.True(ok);
        Assert.NotNull(properties.WeaponProfile);
        Assert.Equal(4, properties.WeaponProfile!.Value.DamageType);
        Assert.Equal(34u, properties.WeaponProfile.Value.WeaponSkill);
        Assert.Equal(12, properties.WeaponProfile.Value.Damage);
        Assert.Equal(0.2d, properties.WeaponProfile.Value.DamageVariance);
        Assert.Null(properties.ArmorProfile);
    }

    [Fact]
    public void TryCaptureProperties_withoutAnAppraisal_leavesBothProfilesNull()
    {
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = 902u,
        });

        bool ok = surface.Objects.TryCaptureProperties(
            902u, out PluginItemProperties properties);

        Assert.True(ok);
        Assert.Null(properties.WeaponProfile);
        Assert.Null(properties.ArmorProfile);
    }

    [Fact]
    public void TryCaptureProperties_returnsEveryArmorModAndTheArmorLevelSeparately()
    {
        // Eight DISTINCT values, one per protection field, so a transposed
        // pair (Cold/Fire is the known trap -- they sit next to each other
        // in both the wire blob and the constructor) shows up as a failing
        // assertion instead of two equal numbers hiding the swap. ArmorLevel
        // is asserted separately because it comes from PropertyInt, not the
        // ArmorProfile blob itself.
        var (runtime, commands) = CreateRealSession();
        using var runtimeDisposal = runtime;
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        commands.Start(runtime.Generation);
        var armor = new ClientArmorProfile(
            SlashingProtection: 1.1f,
            PiercingProtection: 1.2f,
            BludgeoningProtection: 1.3f,
            ColdProtection: 1.4f,
            FireProtection: 1.5f,
            AcidProtection: 1.6f,
            NetherProtection: 1.7f,
            LightningProtection: 1.8f);
        var item = new ClientObject
        {
            ObjectId = 903u,
            ArmorProfile = armor,
        };
        item.Properties.Ints[(uint)AcDream.Core.Properties.PropertyInt.ArmorLevel] = 500;
        runtime.InventoryOwner.Objects.AddOrUpdate(item);

        bool ok = surface.Objects.TryCaptureProperties(
            903u, out PluginItemProperties properties);

        Assert.True(ok);
        Assert.NotNull(properties.ArmorProfile);
        PluginArmorProfile projected = properties.ArmorProfile!.Value;
        Assert.Equal(500, projected.ArmorLevel);
        Assert.Equal(1.1f, projected.SlashMod);
        Assert.Equal(1.2f, projected.PierceMod);
        Assert.Equal(1.3f, projected.BludgeonMod);
        Assert.Equal(1.4f, projected.ColdMod);
        Assert.Equal(1.5f, projected.FireMod);
        Assert.Equal(1.6f, projected.AcidMod);
        Assert.Equal(1.7f, projected.NetherMod);
        Assert.Equal(1.8f, projected.ElectricMod);
    }

    /// <summary>
    /// The shop terms and the per-listing stack ceiling reach a plugin
    /// through the host it actually holds, not just through the adapter:
    /// a plugin planning a vendor visit asks host.Automation.Vendor.
    /// Mutation: return default from RuntimeVendorAutomation.Profile and this
    /// goes red on BuyRate.
    /// </summary>
    [Fact]
    public void GraphicalHostProjectsTheVendorShopTermsAndListingStackCeiling()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            new VendorShopProfile(
                MerchandiseItemTypes: (uint)ItemType.SpellComponents,
                MerchandiseMinValue: 25u,
                MerchandiseMaxValue: 30_000u,
                DealMagicalItems: true,
                BuyPrice: 0.75f,
                SellPrice: 1.15f,
                AlternateCurrencyWcid: 0u,
                AlternateCurrencyAmount: 0u,
                AlternateCurrencyPluralName: string.Empty),
            [
                new VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 100,
                    WeenieClassId: 1234u,
                    Name: "Fixture Peas",
                    ItemType: (uint)ItemType.SpellComponents,
                    IconId: 0x06000001u,
                    Value: 5,
                    DescStackSize: 1,
                    MaxStackSize: 25),
            ]);
        IPluginHost host = new AppPluginHost(
            new TestPluginLogger(), new WorldGameState(), new WorldEvents(),
            new SelectionState(), NoOpUiRegistry.Instance, surface);

        PluginVendorProfile profile = host.Automation.Vendor.Profile;
        Assert.Equal(0.75f, profile.BuyRate);
        Assert.Equal((uint)ItemType.SpellComponents, profile.DealsInItemTypes);
        Assert.Equal(25u, profile.MinimumValue);
        Assert.Equal(30_000u, profile.MaximumValue);
        Assert.True(profile.DealsInMagicalItems);
        Assert.False(profile.UsesAlternateCurrency);

        PluginVendorItem item = Assert.Single(host.Automation.Vendor.Items);
        Assert.Equal(25, item.MaxStackSize);
        Assert.Equal((uint)ItemType.SpellComponents, item.ItemType);
    }

    [Fact]
    public void LogoutIsUnavailableWithoutAnInWorldSessionAndNeverCallsTheRoute()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        int calls = 0;
        surface.BindLogout(
            () =>
            {
                calls++;
                return true;
            },
            () => true);

        // The runtime never reached RuntimeLifecycleState.InWorld in this
        // fixture (that requires a driven session), so IsAvailable is false
        // and Logout must refuse without ever touching the bound route --
        // matching every other automation command's IsAvailable gate.
        Assert.False(((ILoginAutomation)surface).Logout());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DialogsAnswerForwardsToTheBoundRoute()
    {
        using var surface = new RuntimeAutomationSurface();
        uint? seenContext = null;
        bool? seenAccept = null;
        surface.BindDialogs((contextId, accept) =>
        {
            seenContext = contextId;
            seenAccept = accept;
            return true;
        });

        bool result = ((IDialogAutomation)surface).Answer(42u, true);

        Assert.True(result);
        Assert.Equal(42u, seenContext);
        Assert.True(seenAccept);
    }

    [Fact]
    public void DialogsAnswerReturnsFalseWithoutABoundRoute()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.False(((IDialogAutomation)surface).Answer(1u, true));
    }

    [Fact]
    public void RaiseConfirmationRequestedFiresTheEvent()
    {
        var events = new WorldEvents();
        using var surface = new RuntimeAutomationSurface(events);
        PluginConfirmation? seen = null;
        events.ConfirmationRequested += c => seen = c;

        surface.RaiseConfirmationRequested(
            new PluginConfirmation(7u, 5, "Continue?"));

        Assert.Equal(new PluginConfirmation(7u, 5, "Continue?"), seen);
    }

    // Enter/Leave drive a real GameRuntime + LiveSessionController +
    // LiveSessionHost + DirectGameRuntimeCommandAdapter through Start()/Stop()
    // -- the exact production command boundary the headless host uses --
    // rather than fabricating an EmitLifecycle call directly. OnLifecycle
    // must fire off the same real path production hosts use
    // (GameRuntime.SyncLifecycleEmission), not off a synthetic delta a real
    // session would never produce on its own.
    private static (GameRuntime Runtime, DirectGameRuntimeCommandAdapter Commands) CreateRealSession(
        Action<RuntimeGenerationToken>? reset = null,
        RealSessionOperations? operations = null)
    {
        operations ??= new RealSessionOperations();
        GameRuntime runtime = GameRuntimeTestFactory.Create(session: operations);
        var session = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new NoOpEventRoute(),
                    _ => new NoOpCommandRoute()),
                reset ?? (_ => { }),
                new LiveSessionSelectionBindings(
                    id => runtime.PlayerIdentity.ServerGuid = id,
                    _ => { },
                    _ => { },
                    _ => { },
                    _ => { },
                    () => { }),
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
            new LiveSessionConnectOptions(
                true,
                "127.0.0.1",
                9000,
                "plugin-api-user",
                "plugin-api-password"),
            runtime: runtime);
        var commands = new DirectGameRuntimeCommandAdapter(runtime, session);
        return (runtime, commands);
    }

    private static void Enter(GameRuntime runtime, DirectGameRuntimeCommandAdapter commands) =>
        commands.Start(runtime.Generation);

    private static void Leave(GameRuntime runtime, DirectGameRuntimeCommandAdapter commands) =>
        commands.Stop(runtime.Generation);

    private sealed class RealSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint)
        {
            var session = new WorldSession(endpoint, new NoOpTransport());
            // This connection is never negotiated, so a reliable send has no
            // cipher to go out under. A client that has arrived in the world
            // does send -- it asks the server about the allegiance -- so the
            // send is taken here, the way every other test over a connection
            // with no wire under it takes one.
            session.GameMessageCapture = (_, _) => { };
            return session;
        }

        public void Connect(WorldSession session, string user, string password) { }

        public List<CharacterList.Character> Roster { get; } =
            [new CharacterList.Character(0x50000001u, "PluginApiFixture", 0u)];

        public bool Lost { get; set; }

        public bool IsConnectionLost(WorldSession session) => Lost;

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [.. Roster],
                [],
                11,
                "PluginApi",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }

        public void ReturnToCharacterSelect(WorldSession session) { }

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    private sealed class LeavingObserver : IRuntimeEventObserver
    {
        public List<string> Log { get; } = [];

        public void Clear() => Log.Clear();

        public void OnLeavingWorld() => Log.Add("leaving");
        public void OnLifecycle(in RuntimeLifecycleDelta delta) =>
            Log.Add($"{delta.Previous}->{delta.Current}");
        public void OnCommand(in RuntimeCommandDelta delta) { }
        public void OnEntity(in RuntimeEntityDelta delta) { }
        public void OnInventory(in RuntimeInventoryDelta delta) { }
        public void OnChat(in RuntimeChatDelta delta) { }
        public void OnMovement(in RuntimeMovementDelta delta) { }
        public void OnPortal(in RuntimePortalDelta delta) { }
        public void OnCombat(in RuntimeCombatDelta delta) { }
    }

    private sealed class TestPluginLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class NoOpTransport : IWorldSessionTransport
    {
        public void Send(ReadOnlySpan<byte> datagram) { }
        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram) { }

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
            throw new OperationCanceledException(cancellationToken);

        public void Dispose() { }
    }

    private sealed class NoOpEventRoute : ILiveSessionEventRouting
    {
        public void Attach() { }
        public void Dispose() { }
    }

    private sealed class NoOpCommandRoute : ILiveSessionCommandRouting
    {
        public void Activate() { }
        public void Dispose() { }
    }

    private static SpellMetadata Spell(uint spellId, string name) =>
        new(
            SpellId: spellId,
            Name: name,
            School: "Life",
            Family: 0u,
            IconId: 0u,
            SpellWords: "",
            Duration: 60f,
            ManaCost: 0,
            IsDebuff: false,
            IsFellowship: false,
            Description: "",
            SortKey: 0,
            Difficulty: 0,
            Flags: 0u,
            Generation: 1,
            IsFastWindup: false,
            IsOffensive: false,
            IsUntargeted: false,
            Speed: 0f,
            CasterEffect: 0u,
            TargetEffect: 0u,
            TargetMask: 0u,
            SpellType: 0);
}
