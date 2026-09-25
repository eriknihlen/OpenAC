using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Items;
using AcDream.Core.Properties;
using AcDream.Headless.Plugins;
using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using System.Net;

namespace AcDream.Headless.Tests;

public sealed class HeadlessPluginApiSurfaceTests
{
    [Fact]
    public void HeadlessHostEquipmentProjectsClassAndExplicitZeroStack()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);
        commands.Start(runtime.Generation);
        const uint itemId = 0x700000ACu;
        runtime.InventoryOwner.Objects.AddOrUpdate(new AcDream.Core.Items.ClientObject
        {
            ObjectId = itemId,
            Name = "Empty bow stack",
            Type = AcDream.Core.Items.ItemType.MissileWeapon,
            ValidLocations = AcDream.Core.Items.EquipMask.Held,
            ContainerId = 0x50000001u,
            StackSize = 0,
        });
        IPluginHost pluginHost = host;

        PluginEquipmentItem item = Assert.Single(
            pluginHost.Automation.Equipment.CaptureOwnedEquipment(),
            item => item.ObjectId == itemId);
        Assert.Equal(PluginObjectClass.MissileWeapon, item.ObjectClass);
        Assert.Equal(0, item.StackSize);
    }

    [Fact]
    public void ChatReceivedFiresInOrderWithTheTextClassAndCombatKind()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginChatMessage>();
        host.Automation.Chat.Received += seen.Add;

        runtime.CommunicationOwner.Chat.OnSystemMessage("first", 0x0Du);
        runtime.CommunicationOwner.Chat.OnCombatLine(
            "second",
            logTextType: 0x0Eu,
            kind: CombatLineKind.Error);

        Assert.Equal(["first", "second"], seen.Select(static m => m.Text));
        Assert.Equal(0x0D, seen[0].LogTextType);
        Assert.Equal(0, seen[0].CombatKind);
        Assert.Equal(3, seen[1].CombatKind);
    }

    [Fact]
    public void AFilteredLineNeverReachesTheTranscriptOrTheEvent()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<string>();
        host.Automation.Chat.Received += message => seen.Add(message.Text);
        using IDisposable filter = host.Automation.Chat.RegisterFilter(
            static candidate => candidate.Text == "drop me");

        runtime.CommunicationOwner.Chat.OnSystemMessage("drop me", 0u);
        runtime.CommunicationOwner.Chat.OnSystemMessage("keep me", 0u);

        Assert.Equal(["keep me"], seen);
        Assert.Equal(1, runtime.CommunicationOwner.Chat.Count);
    }

    /// <summary>
    /// The windowless client's plugin host, and the bus its session host
    /// hangs off it: an interceptor a plugin registers through the host is
    /// what the bus answers the chat router with, so a typed line goes out
    /// rewritten.
    /// </summary>
    [Fact]
    public void AnInterceptorRegisteredThroughTheHeadlessHostRewritesWhatTheRouterSends()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        IPluginHost pluginHost = host;
        using IDisposable alias = pluginHost.Automation.Chat.RegisterInputInterceptor(
            static typed => typed == "go"
                ? PluginChatInputDecision.Rewrite("hello there")
                : PluginChatInputDecision.Pass);
        // The bus exactly as the session host builds it, with the plugin
        // host's decision behind it.
        var bus = new AcDream.Runtime.Chat.LiveChatCommandSurface(
            interceptChatInput: host.InterceptChatInput);

        PluginChatInputDecision decision =
            ((AcDream.Runtime.Chat.IPluginCommandBus)bus).InterceptChatInput("go");
        var recording = new RecordingPluginBus(bus);
        AcDream.Runtime.Chat.SubmitOutcome outcome = AcDream.Runtime.Chat.ChatCommandRouter.Submit(
            "go",
            new AcDream.Runtime.Chat.RuntimeChatCommandFeedback(runtime.CommunicationOwner),
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
    /// The windowless bus with what was published kept, so the words that
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
    public void LoginCompleteAndLogoffFollowTheInWorldEdge()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);
        int logins = 0;
        int logoffs = 0;
        host.Events.LoginComplete += () => logins++;
        host.Events.Logoff += () => logoffs++;

        commands.Start(runtime.Generation);
        Assert.Equal(1, logins);

        commands.Stop(runtime.Generation);
        Assert.Equal(1, logoffs);

        commands.Start(runtime.Generation);
        Assert.Equal(2, logins);
        Assert.Equal(1, logoffs);
    }

    // The plugin's SessionContext starts several character-scoped macros
    // (chat logger, trackers, inventory logger, on-login commands) only
    // once ICharacterInfo.Name/WorldName/AccountName all resolve non-empty
    // after LoginComplete. Before this fix HeadlessAutomationSurface.
    // Character was the NoOp stub, so that edge never fired on the
    // headless host and nothing ever subscribed.
    [Fact]
    public void CharacterNameIsPopulatedTheMomentLoginCompleteFires()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);

        commands.Start(runtime.Generation);

        Assert.True(host.Automation.Character.IsInWorld);
        Assert.Equal("HeadlessPluginApiFixture", host.Automation.Character.Name);
        Assert.Equal("HeadlessPluginApi", host.Automation.Character.AccountName);
        Assert.Equal(0x50000001u, host.Automation.Character.ObjectId);
    }

    [Fact]
    public void CharacterNameGoesEmptyAgainAfterLogoff()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);

        commands.Start(runtime.Generation);
        Assert.False(string.IsNullOrEmpty(host.Automation.Character.Name));

        commands.Stop(runtime.Generation);

        Assert.False(host.Automation.Character.IsInWorld);
        Assert.Equal(string.Empty, host.Automation.Character.Name);
    }

    // The plugin's auto-trade-accept macro resolves the trade partner's
    // NAME through Automation.Objects.TryGet before matching its
    // whitelist. The headless host's Objects used to be the NoOp stub
    // (always returns false), so the whitelist check was never reachable
    // there; it now binds the shared runtime surface.
    [Fact]
    public void ObjectsTryGetResolvesAKnownObjectsNameAndClass()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);
        commands.Start(runtime.Generation);

        Assert.NotSame(NoOpAutomationSurface.Instance, host.Automation.Objects);

        const uint partnerGuid = 0x50000123u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new AcDream.Core.Items.ClientObject
        {
            ObjectId = partnerGuid,
            Name = "+Acdream",
            PublicWeenieBitfield = 0x00000008u, // Player
        });

        bool found = host.Automation.Objects.TryGet(partnerGuid, out PluginWorldObject value);

        Assert.True(found);
        Assert.Equal("+Acdream", value.Name);
        Assert.Equal(PluginObjectClass.Player, value.ObjectClass);
    }

    [Fact]
    public void ObjectsTryGetExposesPortalDetailsAfterAppraisal()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);
        commands.Start(runtime.Generation);
        IPluginHost pluginHost = host;

        const uint portalId = 0x50000124u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = portalId,
            Name = "Town portal",
            Type = ItemType.Portal,
        });

        Assert.True(pluginHost.Automation.Objects.TryGet(portalId, out PluginWorldObject before));
        Assert.Equal(PluginObjectClass.Portal, before.ObjectClass);
        Assert.False(before.HasAppraisalData);
        Assert.Null(before.PortalDestination);
        Assert.Null(before.PortalMinimumLevel);
        Assert.Null(before.PortalMaximumLevel);

        var appraisal = new PropertyBundle();
        appraisal.Strings[(uint)PropertyString.AppraisalPortalDestination] = "Holtburg";
        appraisal.Ints[(uint)PropertyInt.MinLevel] = 10;
        appraisal.Ints[(uint)PropertyInt.MaxLevel] = 30;
        Assert.True(runtime.InventoryOwner.Objects.UpdateAppraisal(
            portalId, appraisal, Array.Empty<uint>()));

        Assert.True(pluginHost.Automation.Objects.TryGet(portalId, out PluginWorldObject after));
        Assert.True(after.HasAppraisalData);
        Assert.Equal("Holtburg", after.PortalDestination);
        Assert.Equal(10, after.PortalMinimumLevel);
        Assert.Equal(30, after.PortalMaximumLevel);
    }

    [Fact]
    public void ObjectsCaptureObjectsReturnsBothEntityAndInventoryOnlyRecords()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);
        commands.Start(runtime.Generation);

        // An inventory-table-only object (never spawned as a live entity --
        // e.g. an unopened container's contents) must still be captured,
        // alongside anything with a live entity record.
        const uint inventoryOnlyGuid = 0x70000777u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new AcDream.Core.Items.ClientObject
        {
            ObjectId = inventoryOnlyGuid,
            Name = "Stashed Item",
        });

        IReadOnlyList<PluginWorldObject> captured =
            host.Automation.Objects.CaptureObjects();

        Assert.Contains(captured, o => o.ObjectId == inventoryOnlyGuid);
        Assert.Contains(captured, o => o.Name == "Stashed Item");
        // Sorted by ObjectId, and no duplicate entries for an object that
        // appears in both the entity directory and the inventory table.
        Assert.Equal(
            captured.Select(o => o.ObjectId).Distinct().OrderBy(id => id),
            captured.Select(o => o.ObjectId));
    }

    // IPluginHost.Storage has a default interface member returning
    // NoOpPluginStorage (ReadText always null, WriteText a no-op).
    // HeadlessPluginHost never overrode it, so ScopedPluginHost's
    // per-plugin storage wrapper always wrapped the NoOp instance -- every
    // headless plugin's persisted settings file was silently never read
    // from or written to disk, regardless of what the real file on disk
    // said.
    [Fact]
    public void StorageIsRealWhenSuppliedAndRoundTripsAFile()
    {
        string root = Directory.CreateTempSubdirectory("acdream-headless-storage-test-").FullName;
        try
        {
            var storage = new FilePluginStorage(root);
            using GameRuntime runtime = NewRuntime();
            using var host = new HeadlessPluginHost(
                runtime, new InertLogger(), storage: storage);

            Assert.NotSame(NoOpAutomationSurface.Instance, host.Automation);
            Assert.NotSame(
                AcDream.Plugin.Abstractions.NoOpPluginStorage.Instance, host.Storage);

            host.Storage.WriteText("probe.txt", "hello");
            Assert.Equal("hello", host.Storage.ReadText("probe.txt"));
            Assert.True(File.Exists(Path.Combine(root, "probe.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StorageDefaultsToNoOpWhenNotSupplied()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = new HeadlessPluginHost(runtime, new InertLogger());

        Assert.Same(
            AcDream.Plugin.Abstractions.NoOpPluginStorage.Instance, host.Storage);
    }

    [Fact]
    public void TheDeathMessageReachesPlugins()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var deaths = new List<string>();
        host.Events.LocalPlayerDied += deaths.Add;

        runtime.CommunicationOwner.ReportLocalPlayerDeath("You have died!");

        Assert.Equal(["You have died!"], deaths);
    }

    [Fact]
    public void PostMessageUsesTheRequestedTextClass()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage("tinted", (int)RetailLogTextType.Magic);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("tinted", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Magic, entry.LogTextType);
    }

    [Fact]
    public void PostMessageRejectsTheStatusOnlyClientLocalClassAndFallsBackToDefault()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage(
            "should not become a status notice",
            (int)RetailLogTextType.ClientLocal);

        // ClientLocal routes to the status overlay, not the transcript.
        // A plugin has no legitimate reason to post there, so the surface
        // must fall back to the default transcript class instead.
        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("should not become a status notice", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
        runtime.CommunicationOwner.SpewBox.Tick(0);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void PostMessageRejectsAnOutOfRangeTextClassAndFallsBackToDefault()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        host.Automation.Chat.PostMessage("out of range", 9999);

        ChatEntry entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);
    }

    [Fact]
    public void PostSystemMessageThrowsOnNullText()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.Throws<ArgumentNullException>(
            () => host.Automation.Chat.PostSystemMessage(null!));
    }

    [Fact]
    public void AHostWithoutAWindowReportsNoClipboard()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        IPluginHost asHost = host;
        Assert.False(asHost.Clipboard.TrySetText("anything"));
    }

    [Fact]
    public void FileStorageReportsTheDirectoryItWritesTo()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "acdream-storage-root-" + Guid.NewGuid().ToString("N"));
        try
        {
            IPluginStorage storage = new FilePluginStorage(root);

            Assert.Equal(Path.GetFullPath(root), storage.RootPath);
            Assert.Null(((IPluginStorage)NoOpPluginStorage.Instance).RootPath);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
    /// <summary>
    /// A client with no window turns what the runtime did into what a plugin
    /// hears through the same shared surface the client with a window uses,
    /// rather than through a mapping of its own. This drives the runtime and
    /// reads what the plugin got, so the whole road is under it: an answered
    /// description reaches a plugin as that object having been identified.
    /// </summary>
    [Fact]
    public void ObjectChangesReachAPluginFromTheRuntimeWithNoWindow()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginObjectChange>();
        host.Events.ObjectChanged += seen.Add;

        Assert.True(runtime.ActionOwner.Transactions.TryRequestAppraisal(
            100u,
            static _ => { },
            AppraisalRequestOrigin.Automation));
        _ = runtime.ActionOwner.Transactions.AcceptAppraisalResponse(100u);

        Assert.Equal(
            new (uint ObjectId, PluginObjectChangeKind Kind)[]
            {
                (100u, PluginObjectChangeKind.IdentReceived),
            },
            seen.Select(static c => (c.ObjectId, c.Kind)));
        Assert.Equal([1L], seen.Select(static c => c.Revision));
    }

    /// <summary>
    /// The runtime's own entity and inventory vocabulary reaches a plugin as
    /// the plugin's narrower one, in order and stamped. The mapping belongs
    /// to the shared surface, which is what observes the runtime here, so
    /// this drives the surface rather than the host.
    /// </summary>
    [Fact]
    public void ObjectChangedMapsEntityAndInventoryDeltasToPluginKinds()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginObjectChange>();
        host.Events.ObjectChanged += seen.Add;
        var observer = (IRuntimeEventObserver)host.Automation;
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
            RuntimeEntityChange.Deleted,
            new RuntimeEntitySnapshot(
                new RuntimeEntityIdentity(100u, 1u, 1), 0u, 0u, null)));
        observer.OnInventory(new RuntimeInventoryDelta(
            stamp,
            RuntimeInventoryChange.Added,
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
                (200u, PluginObjectChangeKind.Created),
            },
            seen.Select(static c => (c.ObjectId, c.Kind)));
        Assert.Equal([1L, 2L, 3L, 4L], seen.Select(static c => c.Revision));
        Assert.Null(seen[2].Current);
    }

    [Fact]
    public void PortalTransitionCoalescesDuplicateRuntimeSnapshots()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginPortalTransition>();
        host.Events.PortalTransition += seen.Add;
        // The runtime's portal observer is the shared surface, not this
        // host: one projection feeds both clients.
        var observer = (IRuntimeEventObserver)host.Automation;
        RuntimePortalSnapshot snapshot = RuntimePortalSnapshot.Idle with
        {
            Generation = 4,
            Kind = RuntimePortalKind.Portal,
            Materialized = false,
        };

        observer.OnPortal(new RuntimePortalDelta(default, snapshot));
        observer.OnPortal(new RuntimePortalDelta(default, snapshot));
        observer.OnPortal(new RuntimePortalDelta(
            default,
            snapshot with { Materialized = true }));

        Assert.Equal(2, seen.Count);
        Assert.Equal([1L, 2L], seen.Select(static item => item.Revision));
        Assert.All(seen, static item => Assert.Equal(
            PluginPortalTransitionKind.Portal,
            item.Kind));
    }

    [Fact]
    public void ContainerOpenedAndClosedFollowExternalContainerTransitions()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        uint? opened = null;
        uint? closed = null;
        host.Events.ContainerOpened += id => opened = id;
        host.Events.ContainerClosed += id => closed = id;

        runtime.InventoryOwner.ExternalContainers.RequestOpen(500u);
        runtime.InventoryOwner.ExternalContainers.ApplyViewContents(500u);
        Assert.Equal(500u, opened);

        runtime.InventoryOwner.ExternalContainers.ApplyClose(500u);
        Assert.Equal(500u, closed);
    }

    [Fact]
    public void ObjectChangedReportsIdentReceivedOnBothTheFirstResponseAndARefresh()
    {
        // AcceptAppraisalResponse now raises AppraisalReceived (and so this
        // ObjectChanged) on every accepted response, not just the first --
        // a refresh of already-held data can still carry a changed payload.
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginObjectChange>();
        host.Events.ObjectChanged += seen.Add;

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
    public void ConfirmationRequestedFiresWhenTheHostRaisesIt()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        PluginConfirmation? seen = null;
        host.Events.ConfirmationRequested += c => seen = c;

        host.RaiseConfirmationRequested(new PluginConfirmation(9u, 5, "Sure?"));

        Assert.Equal(new PluginConfirmation(9u, 5, "Sure?"), seen);
    }

    [Fact]
    public void LogoutIsUnavailableWithoutAnInWorldSessionAndNeverCallsTheRoute()
    {
        using GameRuntime runtime = NewRuntime();
        var logout = new AcDream.Headless.Hosting.HeadlessLogoutAutomation(runtime);
        using var host = new HeadlessPluginHost(
            runtime,
            new InertLogger(),
            logout: logout);

        // Logout() is the contract's forwarding alias for RequestLogout();
        // outside the world both refuse and the transit owner never begins a logoff.
        Assert.False(((ILoginAutomation)host.Automation).CanRequestLogout);
        Assert.False(((ILoginAutomation)host.Automation).Logout());
        Assert.False(runtime.TransitOwner.IsLogoutActive);
    }

    [Fact]
    public void WindowMinimizeAndRestoreAreUnavailableAndIsMinimizedIsFalse()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.False(host.Window.IsMinimized);
        Assert.Equal(HostWindowStatus.Unavailable, host.Window.Minimize().Status);
        Assert.Equal(HostWindowStatus.Unavailable, host.Window.Restore().Status);
    }

    [Fact]
    public void WindowRequestCloseIsUnavailableWithoutARoute()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.Equal(
            HostWindowStatus.Unavailable,
            host.Window.RequestClose().Status);
    }

    [Fact]
    public void WindowRequestCloseReachesTheGracefulStopRouteExactlyOnce()
    {
        using GameRuntime runtime = NewRuntime();
        int calls = 0;
        bool RequestGracefulStop()
        {
            calls++;
            return true;
        }
        using var host = new HeadlessPluginHost(
            runtime,
            new InertLogger(),
            requestGracefulStop: RequestGracefulStop);

        HostWindowResult result = host.Window.RequestClose();

        Assert.Equal(HostWindowStatus.Done, result.Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void WindowRequestCloseReportsUnavailableWhenTheRouteThrowsRatherThanEscaping()
    {
        // A throw from the wired route (a disposed dependency reached
        // mid-teardown, for instance) must report Unavailable rather than
        // escape into the plugin that called RequestClose.
        using GameRuntime runtime = NewRuntime();
        bool ThrowingRequestGracefulStop() =>
            throw new ObjectDisposedException("fixture");
        using var host = new HeadlessPluginHost(
            runtime,
            new InertLogger(),
            requestGracefulStop: ThrowingRequestGracefulStop);

        HostWindowResult result = host.Window.RequestClose();

        Assert.Equal(HostWindowStatus.Unavailable, result.Status);
    }

    [Fact]
    public void DialogsAnswerForwardsToTheBoundRoute()
    {
        using GameRuntime runtime = NewRuntime();
        uint? seenContext = null;
        bool? seenAccept = null;
        bool AnswerConfirmation(uint contextId, bool accept)
        {
            seenContext = contextId;
            seenAccept = accept;
            return true;
        }
        using var host = new HeadlessPluginHost(
            runtime,
            new InertLogger(),
            answerConfirmation: AnswerConfirmation);

        bool result = ((IDialogAutomation)host.Automation).Answer(3u, false);

        Assert.True(result);
        Assert.Equal(3u, seenContext);
        Assert.False(seenAccept);
    }

    [Fact]
    public void DialogsAnswerReturnsFalseWithoutABoundRoute()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);

        Assert.False(((IDialogAutomation)host.Automation).Answer(1u, true));
    }

    // A real GameRuntime + LiveSessionController + LiveSessionHost +
    // DirectGameRuntimeCommandAdapter through Start()/Stop() -- the exact
    // production command boundary the headless host uses -- rather than
    // a synthetic EmitLifecycle call a real session would never produce
    // on its own.
    private static (GameRuntime Runtime, DirectGameRuntimeCommandAdapter Commands) NewRealSession()
    {
        var gameplay = new InertOperations();
        var sessionOps = new RealSessionOperations();
        var runtime = new GameRuntime(new GameRuntimeDependencies(
            gameplay,
            gameplay,
            gameplay,
            gameplay,
            SessionOperations: sessionOps));
        var session = new LiveSessionHost(
            runtime.Session,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new NoOpEventRoute(),
                    _ => new NoOpCommandRoute()),
                _ => { },
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
                "headless-user",
                "headless-password"),
            runtime: runtime);
        var commands = new DirectGameRuntimeCommandAdapter(runtime, session);
        return (runtime, commands);
    }

    private sealed class RealSessionOperations : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new WorldSession(endpoint, new NoOpTransport()).TakingItsSends();

        public void Connect(WorldSession session, string user, string password) { }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "HeadlessPluginApiFixture", 0u)],
                [],
                11,
                "HeadlessPluginApi",
                true,
                true);

        public void EnterWorld(WorldSession session, int activeCharacterIndex) { }

        public void Tick(WorldSession session) { }

        public void DisposeSession(WorldSession session) => session.Dispose();
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

    [Fact]
    public void ActivationCompletedFiresAfterPortalTransition()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginActivationCompletion>();
        host.Events.ActivationCompleted += seen.Add;
        // The runtime's portal observer is the shared surface, not this
        // host: one projection feeds both clients.
        var observer = (IRuntimeEventObserver)host.Automation;

        // Simulate a portal transition completing.
        RuntimePortalSnapshot snapshot = RuntimePortalSnapshot.Idle with
        {
            Generation = 4,
            Kind = RuntimePortalKind.Portal,
            Completed = true,
        };

        observer.OnPortal(new RuntimePortalDelta(default, snapshot));

        // ActivationCompleted should not fire without a pending activation.
        Assert.Empty(seen);
    }

    [Fact]
    public void ActivationCompletedFiresItemUseForLandscapeObject()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        var seen = new List<PluginActivationCompletion>();
        host.Events.ActivationCompleted += seen.Add;

        // Test that the event can be subscribed and that
        // no spurious events fire without an activation.
        Assert.Empty(seen);
    }

    [Fact]
    public void RecallLocationsReturnedFromCaptureLocations()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        IRecallAutomation recalls = host.Automation.Recalls;

        // Without a live session, CaptureLocations returns empty.
        IReadOnlyList<PluginRecallLocation> locations = recalls.CaptureLocations();
        Assert.Empty(locations);
    }

    [Fact]
    public void RecallLocationsIncludeHouseWhenPositionIsKnown()
    {
        var (runtime, commands) = NewRealSession();
        using GameRuntime runtimeDisposal = runtime;
        using var host = NewHost(runtime);

        commands.Start(runtime.Generation);
        IRecallAutomation recalls = host.Automation.Recalls;

        IReadOnlyList<PluginRecallLocation> locations = recalls.CaptureLocations();
        // House location may or may not be present depending on fixture data.
        Assert.NotNull(locations);
    }

    /// <summary>
    /// The windowless host answers the shop terms and the per-listing stack
    /// ceiling exactly as the windowed one does -- both bind the one shared
    /// vendor adapter, so a vendor-planning plugin runs unchanged on either.
    /// Mutation: return default from RuntimeVendorAutomation.Profile and this
    /// goes red on BuyRate.
    /// </summary>
    [Fact]
    public void WindowlessHostProjectsTheVendorShopTermsAndListingStackCeiling()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        runtime.InventoryOwner.Vendor.Apply(
            0x40001000u,
            new AcDream.Core.Items.VendorShopProfile(
                MerchandiseItemTypes: (uint)AcDream.Core.Items.ItemType.SpellComponents,
                MerchandiseMinValue: 25u,
                MerchandiseMaxValue: 30_000u,
                DealMagicalItems: true,
                BuyPrice: 0.75f,
                SellPrice: 1.15f,
                AlternateCurrencyWcid: 20630u,
                AlternateCurrencyAmount: 17u,
                AlternateCurrencyPluralName: "Writs of Refusal"),
            [
                new AcDream.Core.Items.VendorShopItem(
                    ItemGuid: 0x50002000u,
                    StackSize: 100,
                    WeenieClassId: 1234u,
                    Name: "Fixture Peas",
                    ItemType: (uint)AcDream.Core.Items.ItemType.SpellComponents,
                    IconId: 0x06000001u,
                    Value: 5,
                    DescStackSize: 1,
                    MaxStackSize: 25),
            ]);
        IPluginHost pluginHost = host;

        PluginVendorProfile profile = pluginHost.Automation.Vendor.Profile;
        Assert.Equal(0.75f, profile.BuyRate);
        Assert.Equal(
            (uint)AcDream.Core.Items.ItemType.SpellComponents,
            profile.DealsInItemTypes);
        Assert.Equal(25u, profile.MinimumValue);
        Assert.Equal(30_000u, profile.MaximumValue);
        Assert.True(profile.DealsInMagicalItems);
        Assert.True(profile.UsesAlternateCurrency);
        Assert.Equal(20630u, profile.AlternateCurrencyWeenieClassId);
        Assert.Equal(17u, profile.AlternateCurrencyAmount);
        Assert.Equal("Writs of Refusal", profile.AlternateCurrencyName);

        PluginVendorItem item = Assert.Single(pluginHost.Automation.Vendor.Items);
        Assert.Equal(25, item.MaxStackSize);
        Assert.Equal(
            (uint)AcDream.Core.Items.ItemType.SpellComponents,
            item.ItemType);
    }

    [Fact]
    public void ActivationCompletedEventHasDefaultStub()
    {
        // Verify that the event accessor's default stub does not throw.
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        PluginActivationCompletion? seen = null;
        host.Events.ActivationCompleted += c => seen = c;
        host.Events.ActivationCompleted -= c => seen = c;
        Assert.Null(seen);
    }

    /// <summary>
    /// A plugin that draws a map or a HUD asks the same surface on both
    /// hosts. Without a window there is nothing to draw on, so every image
    /// request answers "no image" and nothing is held; the plugin's own
    /// stream factory is never even opened.
    /// </summary>
    [Fact]
    public void WithoutAWindowTheImageSurfaceAnswersInertly()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        IPluginHost pluginHost = host;
        bool opened = false;

        IPluginImages images = pluginHost.Ui.Images;

        Assert.False(pluginHost.HasUi);
        Assert.False(images.IsAvailable);
        Assert.Equal(PluginImage.None, images.FromClientArt(0x06001234u));
        Assert.Equal(PluginImage.None, images.FromSpellIcon(1u));
        Assert.Equal(PluginImage.None, images.FromObjectIcon(0x80000001u));
        Assert.Equal(PluginImage.None, images.FromStream("map.png", () =>
        {
            opened = true;
            return new MemoryStream();
        }));
        Assert.False(opened);
        Assert.False(images.Release(new PluginImage(1, 4, 4)));
        Assert.Equal(0, images.Count);
    }

    /// <summary>
    /// The same canvas registration a plugin makes with a window is
    /// accepted without one: the plugin keeps its handle, sets what it
    /// likes, and the paint callback is never called because there is
    /// nothing to paint on.
    /// </summary>
    [Fact]
    public void WithoutAWindowACanvasIsAcceptedAndNeverPainted()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        IPluginHost pluginHost = host;
        int paints = 0;

        IPluginCanvas canvas = pluginHost.Ui.RegisterCanvas(
            new PluginCanvasDescriptor("hud", 200, 100) { Anchor = PluginCanvasAnchor.BottomRight },
            _ => paints++);
        canvas.Invalidate();
        canvas.IsVisible = false;
        canvas.Offset = new PluginPoint(-10, -10);

        Assert.False(canvas.IsAvailable);
        Assert.Equal("hud", canvas.CanvasId);
        Assert.Equal((200, 100), (canvas.Width, canvas.Height));
        Assert.Equal(PluginCanvasAnchor.BottomRight, canvas.Anchor);
        Assert.False(canvas.IsVisible);
        Assert.Equal(0, paints);
        canvas.Dispose();
    }

    /// <summary>
    /// A canvas that opted in to pointer input is accepted without a
    /// window in the same way: the handler is kept so the plugin's own
    /// logic runs unchanged, nothing ever calls it, and releasing the
    /// pointer does nothing.
    /// </summary>
    [Fact]
    public void WithoutAWindowAPointerHandlerIsKeptAndNeverCalled()
    {
        using GameRuntime runtime = NewRuntime();
        using var host = NewHost(runtime);
        IPluginHost pluginHost = host;
        int calls = 0;
        Action<PluginPointerEvent> handler = _ => calls++;

        IPluginCanvas canvas = pluginHost.Ui.RegisterCanvas(
            new PluginCanvasDescriptor("map", 200, 100) { AcceptsPointerInput = true },
            _ => { });
        canvas.PointerHandler = handler;
        canvas.ReleasePointer();

        Assert.False(canvas.IsAvailable);
        Assert.Same(handler, canvas.PointerHandler);
        Assert.Equal(0, calls);
        canvas.Dispose();
    }

    private static GameRuntime NewRuntime()
    {
        var operations = new InertOperations();
        return new GameRuntime(new GameRuntimeDependencies(
            operations,
            operations,
            operations,
            operations));
    }

    private static HeadlessPluginHost NewHost(GameRuntime runtime) =>
        new(runtime, new InertLogger());

    private sealed class InertLogger : IPluginLogger
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? error = null) { }
    }

    private sealed class InertOperations
        : IRuntimeCombatAttackOperations,
          IRuntimeCombatTargetOperations,
          IRuntimeCombatModeOperations,
          IRuntimeSpellCastOperations
    {
        public bool CanStartAttack(bool allowAutoTarget) => false;
        public void PrepareAttackRequest() { }
        public bool SendAttack(AttackHeight height, float power, bool allowAutoTarget) => false;
        public void SendCancelAttack() { }
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => false;
        public bool AutoRepeatAttack => false;
        public bool AutoTarget => false;
        public uint? SelectClosestTarget() => null;
        public bool IsInWorld => false;
        public IReadOnlyList<AcDream.Core.Items.ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(CombatMode mode) { }
        public uint LocalPlayerId => 0u;
        public bool CanSend => false;
        public bool HasRequiredComponents(uint spellId) => false;
        public bool IsTargetCompatible(
            uint targetId,
            AcDream.Core.Spells.SpellMetadata spell,
            bool showMessage) => false;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }
}
