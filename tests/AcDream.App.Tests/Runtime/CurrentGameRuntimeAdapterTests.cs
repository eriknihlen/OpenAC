using System.Net;
using System.Numerics;
using AcDream.App.Combat;
using AcDream.App.Input;
using AcDream.App.Interaction;
using AcDream.App.Net;
using AcDream.App.Runtime;
using AcDream.App.Spells;
using AcDream.App.Streaming;
using AcDream.App.UI;
using AcDream.App.Update;
using AcDream.App.World;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Social;
using AcDream.Core.Spells;
using AcDream.Core.World;
using AcDream.Runtime;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime.Session;
using AcDream.Runtime.World;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Input;

namespace AcDream.App.Tests.Runtime;

public sealed class CurrentGameRuntimeAdapterTests
{
    [Fact]
    public void AdapterBorrowsCurrentOwnersAndCommandsExecuteAtPressTime()
    {
        using var harness = new Harness();
        var trace = new RuntimeTraceRecorder();
        using IDisposable subscription = harness.Runtime.Subscribe(trace);
        RuntimeGenerationToken initial = harness.Runtime.Generation;

        RuntimeSessionStartResult start = harness.Runtime.Session.Start(initial);

        Assert.Equal(RuntimeSessionStartStatus.Connected, start.Status);
        Assert.Equal(new RuntimeGenerationToken(1), start.Generation);
        Assert.Equal(Harness.PlayerGuid, harness.Identity.ServerGuid);
        Assert.Equal(RuntimeLifecycleState.InWorld, harness.Runtime.Lifecycle.State);
        Assert.Same(harness.Actions.View, harness.Runtime.Actions);

        LiveEntityRecord liveRecord =
            harness.Entities.RegisterAndMaterializeProjection(Spawn(
                Harness.TargetGuid,
                instance: 3,
                cell: 0x12340001u));
        Assert.NotNull(liveRecord.WorldEntity);

        var item = new ClientObject
        {
            ObjectId = Harness.TargetGuid,
            Name = "Runtime fixture",
            StackSize = 4,
            Value = 25,
            ContainerId = Harness.PlayerGuid,
            ContainerSlot = 2,
        };
        harness.Objects.AddOrUpdate(item);
        harness.Chat.OnSystemMessage("runtime parity", 0x1Au);
        RuntimeWorldTransitTestDriver.AcceptPortalDestination(
            harness.WorldTransit,
            0x12340001u);
        Assert.True(harness.WorldReveal.TryBeginPortal(
            1,
            0x12340001u,
            out _));
        WorldRevealReadinessSnapshot readiness =
            harness.WorldReveal.PrepareAndEvaluate(0x12340001u);
        Assert.True(readiness.IsReady);
        harness.Clock.Advance(new UpdateFrameInput(0.125));

        RuntimeGenerationToken generation = harness.Runtime.Generation;
        RuntimeCommandResult selection = harness.Runtime.Selection.Execute(
            generation,
            RuntimeSelectionCommand.SelectClosestHostile);
        RuntimeCommandResult movement =
            ((IGameRuntimeCommands)harness.Runtime).Movement.Execute(
                generation,
                RuntimeMovementCommand.ToggleRunLock);
        RuntimeCommandResult combat = harness.Runtime.Combat.Execute(
            generation,
            RuntimeCombatCommand.ToggleMode);
        RuntimeCommandResult chat =
            ((IGameRuntimeCommands)harness.Runtime).Chat.Execute(
                generation,
                new RuntimeChatCommand(
                    RuntimeChatChannel.General,
                    "hello runtime"));
        RuntimeCommandResult portal =
            ((IGameRuntimeCommands)harness.Runtime).Portal.Execute(
                generation,
                RuntimePortalCommand.RecallLifestone);

        Assert.True(selection.Accepted);
        Assert.True(movement.Accepted);
        Assert.True(combat.Accepted);
        Assert.True(chat.Accepted);
        Assert.True(portal.Accepted);
        Assert.Equal(Harness.TargetGuid, harness.Selection.SelectedObjectId);
        Assert.True(harness.MovementInput.AutoRunActive);
        Assert.Equal(
            [AcDream.Core.Combat.CombatMode.Melee],
            harness.CombatMode.Sent);
        Assert.Equal(
            AcDream.Core.Combat.CombatMode.Melee,
            harness.Actions.Combat.CurrentMode);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is SendChatCmd
            {
                Channel: ChatChannelKind.General,
                Text: "hello runtime",
            });
        Assert.Contains(
            harness.Commands.Published,
            static command => command is ExecuteClientCommandCmd
            {
                Command: ClientCommandId.LifestoneRecall,
            });

        RuntimeStateCheckpoint checkpoint = harness.Runtime.CaptureCheckpoint();
        Assert.Equal(1, checkpoint.EntityCount);
        Assert.Equal(1, checkpoint.InventoryObjectCount);
        Assert.Equal(1, checkpoint.ChatCount);
        Assert.Equal(1L, checkpoint.ChatRevision);
        Assert.Equal(1UL, checkpoint.FrameNumber);
        Assert.Equal(
            Harness.TargetGuid,
            checkpoint.Actions.SelectedObjectId);
        Assert.Equal(1, checkpoint.Actions.SelectionRevision);
        Assert.Equal(RuntimePortalKind.Portal, checkpoint.Portal.Kind);
        Assert.Equal(0x12340001u, checkpoint.Portal.DestinationCell);
        Assert.True(checkpoint.Portal.IsReady);

        Assert.True(harness.Runtime.Entities.TryGet(
            Harness.TargetGuid,
            out RuntimeEntitySnapshot entity));
        Assert.Equal((ushort)3, entity.Identity.Incarnation);
        Assert.True(harness.Runtime.Inventory.TryGet(
            Harness.TargetGuid,
            out RuntimeInventoryItemSnapshot inventory));
        Assert.Equal((ushort)3, inventory.Incarnation);
        Assert.Equal(4, inventory.StackSize);

        RuntimeTraceKind[] kinds = trace.Entries
            .Select(static entry => entry.Kind)
            .ToArray();
        Assert.Equal(RuntimeTraceKind.Command, kinds[0]);
        Assert.Equal(RuntimeTraceKind.Lifecycle, kinds[1]);
        Assert.Contains(RuntimeTraceKind.Inventory, kinds);
        Assert.Contains(RuntimeTraceKind.Chat, kinds);
        Assert.Equal(
            [
                RuntimeCommandDomain.Session,
                RuntimeCommandDomain.Selection,
                RuntimeCommandDomain.Movement,
                RuntimeCommandDomain.Combat,
                RuntimeCommandDomain.Chat,
                RuntimeCommandDomain.Portal,
            ],
            trace.Entries
                .Where(static entry => entry.Kind == RuntimeTraceKind.Command)
                .Select(static entry =>
                    (RuntimeCommandDomain)(entry.Code >> 16))
                .ToArray());
    }

    [Fact]
    public void AdapterBorrowsPreWorldSelectionAndCommandsEnterTheSameRuntimeOwner()
    {
        using var harness = new Harness(awaitCharacterSelection: true);
        RuntimeGenerationToken initial = harness.Runtime.Generation;

        RuntimeSessionStartResult start = harness.Runtime.Session.Start(initial);

        Assert.Equal(
            RuntimeSessionStartStatus.AwaitingCharacterSelection,
            start.Status);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.AwaitingSelection,
            harness.Runtime.CharacterSelection.Snapshot.Lifecycle);
        Assert.Equal(2, harness.Runtime.CharacterSelection.Snapshot.RosterCount);

        RuntimeGenerationToken generation = harness.Runtime.Generation;
        Assert.True(harness.Runtime.CharacterSelectionCommands.Highlight(
            generation,
            0x50000001u).Accepted);
        Assert.True(
            harness.Runtime.CharacterSelection.Snapshot.Buttons.CanRestore);
        Assert.Equal(
            RuntimeCommandStatus.Rejected,
            harness.Runtime.CharacterSelectionCommands.Enter(generation).Status);
        Assert.True(harness.Runtime.CharacterSelectionCommands.Highlight(
            generation,
            Harness.PlayerGuid).Accepted);
        Assert.True(harness.Runtime.CharacterSelectionCommands.Enter(
            generation).Accepted);

        Assert.Equal(RuntimeLifecycleState.InWorld, harness.Runtime.Lifecycle.State);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.InWorld,
            harness.Runtime.CharacterSelection.Snapshot.Lifecycle);
        Assert.Equal(Harness.PlayerGuid, harness.Identity.ServerGuid);
    }

    [Fact]
    public void DisposedAdapterMakesRetainedSelectionRoutesInertWhileRuntimeLives()
    {
        using var harness = new Harness(awaitCharacterSelection: true);
        using CurrentGameRuntimeAdapter survivor =
            harness.CreateAdditionalAdapter();
        Assert.Equal(
            RuntimeSessionStartStatus.AwaitingCharacterSelection,
            harness.Runtime.Session.Start(harness.Runtime.Generation).Status);

        RuntimeGenerationToken generation = harness.Runtime.Generation;
        IRuntimeCharacterSelectionView retainedView =
            harness.Runtime.CharacterSelection;
        IRuntimeCharacterSelectionCommands publicCommands =
            harness.Runtime.CharacterSelectionCommands;
        IRuntimeCharacterSelectionCommands interfaceCommands =
            ((IGameRuntimeCommands)harness.Runtime).CharacterSelection;
        var observer = new CharacterSelectionObserver();
        using IDisposable subscription = retainedView.Subscribe(observer);
        RuntimeCharacterSelectionSnapshot before =
            survivor.CharacterSelection.Snapshot;

        harness.Runtime.Dispose();

        Assert.False(harness.Runtime.Lifecycle.HasTransport);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.Inactive,
            retainedView.Snapshot.Lifecycle);
        Assert.Equal(0, retainedView.Snapshot.RosterCount);
        Assert.False(retainedView.TryGetAt(0, out _));
        Assert.False(retainedView.TryGet(Harness.PlayerGuid, out _));
        var visitor = new CharacterSelectionVisitor();
        retainedView.Visit(visitor);
        Assert.Empty(visitor.Entries);
        Assert.Throws<ObjectDisposedException>(() =>
            retainedView.Subscribe(new CharacterSelectionObserver()));

        RuntimeCommandStatus[] statuses =
        [
            publicCommands.Highlight(generation, 0x50000001u).Status,
            publicCommands.Enter(generation).Status,
            publicCommands.RequestDelete(generation).Status,
            interfaceCommands.ConfirmDelete(generation).Status,
            interfaceCommands.Restore(generation).Status,
            interfaceCommands.Cancel(generation).Status,
        ];
        Assert.All(statuses, status =>
            Assert.Equal(RuntimeCommandStatus.Inactive, status));

        RuntimeCharacterSelectionSnapshot after =
            survivor.CharacterSelection.Snapshot;
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.HighlightedCharacterId, after.HighlightedCharacterId);
        Assert.Equal(before.Operation, after.Operation);

        Assert.True(survivor.CharacterSelectionCommands.Highlight(
            generation,
            0x50000001u).Accepted);
        Assert.Equal(0x50000001u,
            survivor.CharacterSelection.Snapshot.HighlightedCharacterId);
        Assert.Empty(observer.Deltas);
        Assert.Equal(
            RuntimeCharacterSelectionLifecycle.Inactive,
            harness.Runtime.CharacterSelection.Snapshot.Lifecycle);
    }

    [Fact]
    public void GenerationGateRejectsStaleCommandsAndStopAcknowledgesTeardown()
    {
        using var harness = new Harness();
        RuntimeSessionStartResult start =
            harness.Runtime.Session.Start(harness.Runtime.Generation);
        Assert.Equal(RuntimeSessionStartStatus.Connected, start.Status);
        RuntimeGenerationToken active = harness.Runtime.Generation;

        RuntimeTeardownAcknowledgement stopped =
            harness.Runtime.Session.Stop(active);
        RuntimeCommandResult stale = harness.Runtime.Combat.Execute(
            active,
            RuntimeCombatCommand.ToggleMode);

        Assert.True(stopped.IsComplete);
        Assert.Equal(active, stopped.RetiredGeneration);
        Assert.Equal(new RuntimeGenerationToken(2), stopped.CurrentGeneration);
        Assert.Equal(RuntimeCommandStatus.StaleGeneration, stale.Status);
        Assert.Empty(harness.CombatMode.Sent);
        Assert.Equal(RuntimeLifecycleState.Stopped, harness.Runtime.Lifecycle.State);
    }

    [Fact]
    public void GraphicalAndDirectCombatMagicCommands_ReachTheExactSameOwners()
    {
        using var graphical = new Harness();
        using var direct = new Harness();
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            graphical.Runtime.Session.Start(graphical.Runtime.Generation).Status);
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            direct.Runtime.Session.Start(direct.Runtime.Generation).Status);
        var directTrace = new RuntimeTraceRecorder();
        using IDisposable directTraceSubscription =
            direct.Runtime.Subscribe(directTrace);

        var graphicalAttack = new RecordingAttackOperations();
        var directAttack = new RecordingAttackOperations();
        var graphicalSpell = new RecordingSpellOperations();
        var directSpell = new RecordingSpellOperations();
        using IDisposable graphicalAttackBinding =
            graphical.CombatAttackOperations.BindOwned(graphicalAttack);
        using IDisposable directAttackBinding =
            direct.CombatAttackOperations.BindOwned(directAttack);
        using IDisposable graphicalSpellBinding =
            graphical.SpellCastOperations.BindOwned(graphicalSpell);
        using IDisposable directSpellBinding =
            direct.SpellCastOperations.BindOwned(directSpell);

        PrepareCombatAndMagic(graphical);
        PrepareCombatAndMagic(direct);

        var graphicalInput =
            new CombatAttackInputFrameAdapter(graphical.Actions.CombatAttack);
        Assert.True(graphicalInput.HandleInputAction(
            InputAction.CombatLowAttack,
            ActivationType.Press));
        Assert.True(graphical.Actions.View.Snapshot.CombatAttack.RequestInProgress);
        Assert.True(graphicalInput.HandleInputAction(
            InputAction.CombatLowAttack,
            ActivationType.Hold));
        Assert.True(graphical.Actions.View.Snapshot.CombatAttack.RequestInProgress);
        Assert.True(graphicalInput.HandleInputAction(
            InputAction.CombatLowAttack,
            ActivationType.Release));
        Assert.Equal(
            CastRequestResult.Sent,
            graphical.Actions.SpellCast.Cast(1u));

        RuntimeGenerationToken generation = direct.Runtime.Generation;
        Assert.True(direct.Runtime.Combat.ExecuteAttack(
            generation,
            new RuntimeCombatAttackInput(
                RuntimeCombatAttackCommand.LowAttack,
                RuntimeInputActivation.Press)).Accepted);
        Assert.True(direct.Runtime.Combat.ExecuteAttack(
            generation,
            new RuntimeCombatAttackInput(
                RuntimeCombatAttackCommand.LowAttack,
                RuntimeInputActivation.Release)).Accepted);
        Assert.True(direct.Runtime.Magic.Execute(
            generation,
            new RuntimeMagicCommand(1u)).Accepted);

        Assert.Equal(graphicalAttack.Trace, directAttack.Trace);
        Assert.Equal(graphicalSpell.Trace, directSpell.Trace);
        Assert.Equal(
            graphical.Actions.View.Snapshot.CombatAttack,
            direct.Actions.View.Snapshot.CombatAttack);
        Assert.Equal(
            graphical.Actions.View.Snapshot.Magic,
            direct.Actions.View.Snapshot.Magic);
        Assert.Equal(
            [
                RuntimeCommandDomain.Combat,
                RuntimeCommandDomain.Combat,
                RuntimeCommandDomain.Magic,
            ],
            directTrace.Entries
                .Where(static entry => entry.Kind == RuntimeTraceKind.Command)
                .Select(static entry =>
                    (RuntimeCommandDomain)(entry.Code >> 16))
                .ToArray());
    }

    [Fact]
    public void DirectAndGraphicalHosts_ProduceIdenticalEntityObjectTrace()
    {
        WorldSession.EntitySpawn spawn =
            Spawn(Harness.TargetGuid, instance: 7, cell: 0x12340001u);
        var direct = new RuntimeEntityObjectLifetime();
        direct.BindEventContext(static () => default, static () => 0UL);
        var directTrace = new EntityObjectTrace();
        using IDisposable directSubscription =
            direct.Events.Subscribe(directTrace);

        InvalidOperationException directRefusal =
            Assert.Throws<InvalidOperationException>(() =>
                direct.RegisterEntityWithInitialResidence(
                    spawn,
                    isLocalPlayer: false));

        using var graphical = new Harness();
        var graphicalTrace = new EntityObjectTrace();
        using IDisposable graphicalSubscription =
            graphical.EntityObjects.Events.Subscribe(graphicalTrace);

        InvalidOperationException graphicalRefusal =
            Assert.Throws<InvalidOperationException>(() =>
                graphical.Entities.RegisterAndMaterializeProjection(spawn));

        Assert.Contains(
            "cannot acquire a structurally valid initial residence lease",
            directRefusal.Message,
            StringComparison.Ordinal);
        Assert.Equal(directRefusal.Message, graphicalRefusal.Message);
        Assert.Equal(directTrace.Entries, graphicalTrace.Entries);
        Assert.Empty(directTrace.Entries);
        Assert.Equal(0, direct.Entities.Count);
        Assert.Equal(0, direct.Objects.ObjectCount);
        Assert.Equal(0, graphical.EntityObjects.Entities.Count);
        Assert.Equal(0, graphical.Objects.ObjectCount);
    }

    [Fact]
    public void GraphicalObserverFailure_DoesNotStarveLaterObserverOrOwner()
    {
        using var harness = new Harness();
        Assert.Equal(
            RuntimeSessionStartStatus.Connected,
            harness.Runtime.Session.Start(harness.Runtime.Generation).Status);
        var throwing = new ThrowingEntityObserver();
        var recording = new RuntimeTraceRecorder();
        IDisposable first = harness.Runtime.Subscribe(throwing);
        IDisposable second = harness.Runtime.Subscribe(recording);

        LiveEntityRecord record =
            harness.Entities.RegisterAndMaterializeProjection(Spawn(
                Harness.TargetGuid,
                instance: 4,
                cell: 0x12340001u));

        Assert.True(harness.EntityObjects.Entities.IsCurrent(record.Canonical));
        Assert.Contains(
            recording.Entries,
            entry => entry.Kind == RuntimeTraceKind.Entity
                && entry.PrimaryObjectId == Harness.TargetGuid);
        Assert.Equal(1, harness.EntityObjects.Events.SubscriberCount);
        Assert.Equal(0, harness.EntityObjects.Events.DispatchFailureCount);

        first.Dispose();
        second.Dispose();
        Assert.Equal(0, harness.EntityObjects.Events.SubscriberCount);
    }

    [Fact]
    public void AdapterBorrowsExactGameplayViewsAndCheckpointState()
    {
        using var harness = new Harness();
        harness.Character.Options.Replace(0x04000000u, 0x00948700u);
        harness.Character.MovementSkills.Update(200, 175);
        harness.Character.Spellbook.OnSpellLearned(42u);
        harness.InventoryState.Shortcuts.Load(
        [
            new ShortcutEntry(1, 0x80000001u, 0u),
        ]);
        harness.Communication.Friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Full,
            [
                new FriendEntry(
                    0x50000003u,
                    "Borrowed",
                    true,
                    false,
                    [],
                    []),
            ]));

        Assert.Same(harness.Character.View, harness.Runtime.Character);
        Assert.Same(
            harness.InventoryState.View,
            harness.Runtime.InventoryState);
        Assert.Same(
            harness.Communication.SocialView,
            harness.Runtime.Social);

        RuntimeStateCheckpoint checkpoint =
            harness.Runtime.CaptureCheckpoint();
        Assert.Equal(1, checkpoint.Character.LearnedSpellCount);
        Assert.True(checkpoint.Character.MovementSkills.IsComplete);
        Assert.Equal(1, checkpoint.InventoryState.ShortcutCount);
        Assert.Equal(1, checkpoint.Social.FriendCount);
    }

    [Fact]
    public void GameplayStateCommandsAreGenerationGatedAndReachCurrentTransport()
    {
        using var harness = new Harness();
        var trace = new RuntimeTraceRecorder();
        using IDisposable subscription = harness.Runtime.Subscribe(trace);
        _ = harness.Runtime.Session.Start(harness.Runtime.Generation);
        RuntimeGenerationToken generation = harness.Runtime.Generation;
        IGameRuntimeCommands commands = harness.Runtime;

        RuntimeCommandResult shortcut = commands.InventoryState.AddShortcut(
            generation,
            new RuntimeShortcutCommand(2, 0x80000001u, 0u));
        RuntimeCommandResult favorite = commands.Spellbook.AddFavorite(
            generation,
            tabIndex: 0,
            position: 0,
            spellId: 42u);
        RuntimeCommandResult filter = commands.Spellbook.SetFilter(
            generation,
            0x3FFEu);
        RuntimeCommandResult desired = commands.Spellbook.SetDesiredComponent(
            generation,
            0x68000001u,
            10u);
        RuntimeCommandResult advancement = commands.Character.Advance(
            generation,
            new RuntimeAdvancementCommand(
                RuntimeAdvancementKind.Skill,
                StatId: 6u,
                Cost: 500u));
        RuntimeCommandResult options = commands.Character.SetSingleOption(
            generation,
            0x26u,
            true);
        RuntimeCommandResult friend = commands.Social.Execute(
            generation,
            new RuntimeFriendCommand(
                RuntimeFriendCommandKind.Add,
                Name: "Runtime Friend"));
        RuntimeCommandResult squelch = commands.Social.Execute(
            generation,
            new RuntimeSquelchCommand(
                RuntimeSquelchScope.Global,
                Add: true,
                MessageType: 3u));

        Assert.All(
            new[]
            {
                shortcut,
                favorite,
                filter,
                desired,
                advancement,
                options,
                friend,
                squelch,
            },
            static result => Assert.True(result.Accepted));
        Assert.Contains(
            harness.Commands.Published,
            static command => command is AddShortcutRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is AddFavoriteRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is SetSpellbookFilterRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is SetDesiredComponentRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is RaiseSkillRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is SetSingleCharacterOptionRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is AddFriendRuntimeCmd);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is ModifyGlobalSquelchRuntimeCmd);
        Assert.Contains(
            trace.Entries,
            static entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.InventoryState);
        Assert.Contains(
            trace.Entries,
            static entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.Spellbook);
        Assert.Contains(
            trace.Entries,
            static entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.Character);
        Assert.Contains(
            trace.Entries,
            static entry => entry.Kind == RuntimeTraceKind.Command
                && (entry.Code >> 16)
                    == (int)RuntimeCommandDomain.Social);
        Assert.True(harness.InventoryState.View.TryGetShortcut(
            2,
            out RuntimeShortcutSnapshot storedShortcut));
        Assert.Equal(0x80000001u, storedShortcut.ObjectId);
        Assert.True(harness.Character.View.TryGetFavorite(
            0,
            0,
            out uint storedFavorite));
        Assert.Equal(42u, storedFavorite);
        Assert.Equal(0x3FFEu, harness.Character.Spellbook.SpellbookFilters);
        Assert.True(harness.Character.View.TryGetDesiredComponent(
            0x68000001u,
            out uint storedDesired));
        Assert.Equal(10u, storedDesired);

        int published = harness.Commands.Published.Count;
        RuntimeCommandResult stale = commands.Character.SetSingleOption(
            new RuntimeGenerationToken(generation.Value - 1),
            0x26u,
            false);
        Assert.Equal(RuntimeCommandStatus.StaleGeneration, stale.Status);
        Assert.Equal(published, harness.Commands.Published.Count);
    }


    [Fact]
    public void SetSingleOption_UnknownId_RejectsWithoutPublishing()
    {
        using var harness = new Harness();
        _ = harness.Runtime.Session.Start(harness.Runtime.Generation);
        RuntimeGenerationToken generation = harness.Runtime.Generation;
        IGameRuntimeCommands commands = harness.Runtime;
        int published = harness.Commands.Published.Count;

        RuntimeCommandResult result = commands.Character.SetSingleOption(
            generation,
            0x36u,
            true);

        Assert.Equal(RuntimeCommandStatus.Rejected, result.Status);
        Assert.Equal(published, harness.Commands.Published.Count);
    }

    [Fact]
    public void SaveOptions_PublishesSaveCharacterOptionsCommand()
    {
        using var harness = new Harness();
        _ = harness.Runtime.Session.Start(harness.Runtime.Generation);
        RuntimeGenerationToken generation = harness.Runtime.Generation;
        IGameRuntimeCommands commands = harness.Runtime;

        RuntimeCommandResult result = commands.Character.SaveOptions(generation);

        Assert.True(result.Accepted);
        Assert.Contains(
            harness.Commands.Published,
            static command => command is SaveCharacterOptionsRuntimeCmd);
    }

    [Fact]
    public void GraphicalAndNoWindowGameplayCommandsProduceIdenticalCanonicalState()
    {
        using var directEntities = new RuntimeEntityObjectLifetime();
        using var directInventory =
            new RuntimeInventoryState(directEntities);
        using var directCharacter = new RuntimeCharacterState();
        using var harness = new Harness();
        _ = harness.Runtime.Session.Start(harness.Runtime.Generation);
        RuntimeGenerationToken generation = harness.Runtime.Generation;
        IGameRuntimeCommands graphical = harness.Runtime;
        var directOutbound = new List<string>();
        var shortcut = new ShortcutEntry(2, 0x80000001u, 0u);

        Assert.True(directInventory.TryAddShortcut(
            shortcut,
            () => directOutbound.Add("add-shortcut")));
        Assert.True(directCharacter.TryAddFavorite(
            0,
            0,
            42u,
            () => directOutbound.Add("add-favorite")));
        directCharacter.SetSpellbookFilter(
            0x3FFEu,
            () => directOutbound.Add("filter"));
        Assert.True(directCharacter.TrySetDesiredComponent(
            0x68000001u,
            10u,
            () => directOutbound.Add("desired")));

        Assert.True(graphical.InventoryState.AddShortcut(
            generation,
            new RuntimeShortcutCommand(
                shortcut.Index,
                shortcut.ObjectId,
                shortcut.SpellId)).Accepted);
        Assert.True(graphical.Spellbook.AddFavorite(
            generation,
            0,
            0,
            42u).Accepted);
        Assert.True(graphical.Spellbook.SetFilter(
            generation,
            0x3FFEu).Accepted);
        Assert.True(graphical.Spellbook.SetDesiredComponent(
            generation,
            0x68000001u,
            10u).Accepted);

        Assert.Equal(
            directInventory.View.Snapshot,
            harness.InventoryState.View.Snapshot);
        Assert.Equal(
            directCharacter.View.Snapshot,
            harness.Character.View.Snapshot);
        Assert.Equal(
            directInventory.Shortcuts.Items,
            harness.InventoryState.Shortcuts.Items);
        Assert.Equal(
            directCharacter.Spellbook.GetFavorites(0),
            harness.Character.Spellbook.GetFavorites(0));
        Assert.Equal(
            ["add-shortcut", "add-favorite", "filter", "desired"],
            directOutbound);

        Assert.True(directInventory.TryRemoveShortcut(
            2,
            () => directOutbound.Add("remove-shortcut")));
        Assert.True(directCharacter.TryRemoveFavorite(
            0,
            42u,
            () => directOutbound.Add("remove-favorite")));
        directCharacter.ClearDesiredComponents(
            () => directOutbound.Add("clear-desired"));
        Assert.True(graphical.InventoryState.RemoveShortcut(
            generation,
            2).Accepted);
        Assert.True(graphical.Spellbook.RemoveFavorite(
            generation,
            0,
            42u).Accepted);
        Assert.True(graphical.Spellbook.ClearDesiredComponents(
            generation).Accepted);

        Assert.Equal(
            directInventory.View.Snapshot,
            harness.InventoryState.View.Snapshot);
        Assert.Equal(
            directCharacter.View.Snapshot,
            harness.Character.View.Snapshot);
        Assert.Empty(harness.InventoryState.Shortcuts.Items);
        Assert.Empty(harness.Character.Spellbook.GetFavorites(0));
        Assert.Empty(harness.Character.Spellbook.DesiredComponents);
    }

    private static ClientObject Object(WorldSession.EntitySpawn spawn) => new()
    {
        ObjectId = spawn.Guid,
        Name = spawn.Name ?? string.Empty,
        StackSize = 4,
        Value = 25,
        ContainerId = Harness.PlayerGuid,
        ContainerSlot = 2,
    };

    private static WorldSession.EntityPositionUpdate Position(
        uint guid,
        ushort instance) =>
        new(
            guid,
            new CreateObject.ServerPosition(
                0x12350001u,
                30f,
                40f,
                8f,
                1f,
                0f,
                0f,
                0f),
            Velocity: new Vector3(1f, 2f, 3f),
            PlacementId: null,
            IsGrounded: true,
            InstanceSequence: instance,
            PositionSequence: 2,
            TeleportSequence: 0,
            ForcePositionSequence: 0);

    private sealed class Harness : IDisposable
    {
        public const uint PlayerGuid = 0x50000002u;
        public const uint TargetGuid = 0x70000001u;

        private readonly ItemInteractionController _items;
        private readonly LiveSessionController _session;
        private readonly IDisposable _combatModeBinding;
        private readonly GameRuntime _gameRuntime;
        private readonly SelectionInteractionController _selectionController;

        public Harness(bool awaitCharacterSelection = false)
        {
            CombatAttackOperations = new CombatAttackOperationsSlot();
            CombatModeOperations = new RuntimeCombatModeOperationsSlot();
            SpellCastOperations = new RuntimeSpellCastOperationsSlot();
            Options = LiveOptions();
            Commands = new RecordingCommandRouting();
            Transport = new TestTransport();
            _gameRuntime = GameRuntimeTestFactory.Create(
                CombatAttackOperations,
                new RuntimeCombatTargetOperationsSlot(),
                CombatModeOperations,
                SpellCastOperations,
                new SessionOperations(Transport),
                combatTime: static () => 0d);
            _session = _gameRuntime.Session;
            Identity = new LocalPlayerIdentityState(
                _gameRuntime.PlayerIdentity);
            Entities = new LiveEntityRuntime(
                new GpuWorldState(),
                new NoopEntityResources(),
                EntityObjects);
            CombatMode = new RecordingCombatModeOperations();
            _combatModeBinding =
                CombatModeOperations.BindOwned(CombatMode);
            MovementInput = new DispatcherMovementInputSource(MovementState);
            GameplayInput = new GameplayInputFrameController(
                dispatcher: null,
                MovementInput,
                mouseLook: null,
                new NoopCombatInput());
            Clock = new UpdateFrameClock(_gameRuntime.Clock);
            WorldReveal = new WorldRevealCoordinator(
                WorldTransit,
                static () => new StreamingRevealWindow(1, 1),
                static (_, _, _) => true,
                static _ => true,
                static (_, _) => true,
                static () => true,
                static (_, _) => { },
                static () => { },
                static _ => false);

            _items = new ItemInteractionController(
                Objects,
                Actions.Transactions,
                Actions.Interaction,
                () => PlayerGuid,
                sendUse: null,
                sendUseWithTarget: null,
                sendWield: null,
                sendDrop: null);
            var query = new SelectionQuery(TargetGuid);
            _selectionController = new SelectionInteractionController(
                Selection,
                query,
                _items,
                new SelectionTransport(() => _session?.IsInWorld == true),
                new NoopInteractionMovement(),
                _gameRuntime.ActionOwner.CombatTarget);

            Host = CreateHost(
                _session,
                Commands,
                Identity,
                awaitCharacterSelection);
            Runtime = new CurrentGameRuntimeAdapter(
                _gameRuntime,
                Host,
                Commands,
                _selectionController);
        }

        public RuntimeOptions Options { get; }
        public LocalPlayerIdentityState Identity { get; }
        public RuntimeEntityObjectLifetime EntityObjects =>
            _gameRuntime.EntityObjects;
        public RuntimeInventoryState InventoryState =>
            _gameRuntime.InventoryOwner;
        public RuntimeCharacterState Character =>
            _gameRuntime.CharacterOwner;
        public LiveEntityRuntime Entities { get; }
        public ClientObjectTable Objects => EntityObjects.Objects;
        public RuntimeCommunicationState Communication =>
            _gameRuntime.CommunicationOwner;
        public ChatLog Chat => Communication.Chat;
        public CombatAttackOperationsSlot CombatAttackOperations { get; }
        public RuntimeCombatModeOperationsSlot CombatModeOperations { get; }
        public RuntimeSpellCastOperationsSlot SpellCastOperations { get; }
        public RuntimeActionState Actions => _gameRuntime.ActionOwner;
        public SelectionState Selection => Actions.Selection;
        public RuntimeLocalPlayerMovementState MovementState =>
            _gameRuntime.MovementOwner;
        public RuntimeWorldEnvironmentState Environment =>
            _gameRuntime.EnvironmentOwner;
        public DispatcherMovementInputSource MovementInput { get; }
        public GameplayInputFrameController GameplayInput { get; }
        public RecordingCombatModeOperations CombatMode { get; }
        public UpdateFrameClock Clock { get; }
        public RuntimeWorldTransitState WorldTransit =>
            _gameRuntime.TransitOwner;
        public WorldRevealCoordinator WorldReveal { get; }
        public RecordingCommandRouting Commands { get; }
        public TestTransport Transport { get; }
        public LiveSessionHost Host { get; }
        public CurrentGameRuntimeAdapter Runtime { get; }

        public CurrentGameRuntimeAdapter CreateAdditionalAdapter() =>
            new(
                _gameRuntime,
                Host,
                Commands,
                _selectionController);

        public void Dispose()
        {
            Runtime.Dispose();
            _items.Dispose();
            _combatModeBinding.Dispose();
            Entities.Clear();
            WorldReveal.ResetSession();
            _gameRuntime.Dispose();
        }
    }

    private sealed class CharacterSelectionVisitor
        : IRuntimeCharacterSelectionVisitor
    {
        public List<RuntimeCharacterSelectionEntry> Entries { get; } = [];

        public void Visit(in RuntimeCharacterSelectionEntry character) =>
            Entries.Add(character);
    }

    private sealed class CharacterSelectionObserver
        : IRuntimeCharacterSelectionObserver
    {
        public List<RuntimeCharacterSelectionDelta> Deltas { get; } = [];

        public void OnCharacterSelectionChanged(
            in RuntimeCharacterSelectionDelta delta) =>
            Deltas.Add(delta);
    }

    private static void PrepareCombatAndMagic(Harness harness)
    {
        harness.Actions.Combat.SetCombatMode(AcDream.Core.Combat.CombatMode.Melee);
        harness.Actions.CombatAttack.SetDesiredPower(0f);
        const string csv =
            "Spell ID,Name,Flags [Hex],IsUntargetted,TargetMask [Hex]\n"
            + "1,Runtime Test Spell,0x0,true,0x0";
        harness.Character.InstallSpellMetadata(
            SpellTable.LoadFromReader(new System.IO.StringReader(csv)));
        harness.Character.Spellbook.OnSpellLearned(1u);
    }

    private sealed class RecordingAttackOperations
        : IRuntimeCombatAttackOperations
    {
        public List<string> Trace { get; } = [];
        public bool IsDualWield => false;
        public bool PlayerReadyForAttack => true;
        public bool AutoRepeatAttack => false;

        public bool CanStartAttack() => true;

        public void PrepareAttackRequest() => Trace.Add("prepare");

        public bool SendAttack(
            AcDream.Core.Combat.AttackHeight height,
            float power)
        {
            Trace.Add($"attack:{height}:{power}");
            return true;
        }

        public void SendCancelAttack() => Trace.Add("cancel");
    }

    private sealed class RecordingSpellOperations
        : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => Harness.PlayerGuid;
        public bool CanSend => true;
        public List<string> Trace { get; } = [];

        public bool HasRequiredComponents(uint spellId) => true;

        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;

        public void StopCompletely() => Trace.Add("stop");
        public void SendUntargeted(uint spellId) =>
            Trace.Add($"untargeted:{spellId}");
        public void SendTargeted(uint targetId, uint spellId) =>
            Trace.Add($"targeted:{targetId}:{spellId}");
        public void DisplayMessage(string message) =>
            Trace.Add($"message:{message}");
        public void IncrementBusy() => Trace.Add("busy");
    }

    private static LiveSessionHost CreateHost(
        LiveSessionController controller,
        RecordingCommandRouting commands,
        LocalPlayerIdentityState identity,
        bool awaitCharacterSelection = false)
    {
        Action noop = static () => { };
        var reset = new LiveSessionResetBindings
        {
            MouseCapture = noop,
            PlayerPresentation = noop,
            TeleportPresentation = noop,
            WorldAudio = noop,
            SessionDialogs = noop,
            SettingsCharacterContext = noop,
            EquippedChildren = noop,
            InteractionPresentation = noop,
            SelectionPresentation = noop,
            ParticleVisibility = noop,
            InboundEventFifo = noop,
            LiveLiveness = noop,
            RuntimeGeneration = _ => { },
            SessionIdentityPresentation = _ => { },
            NetworkEffects = noop,
            AnimationHookFrames = noop,
            LivePresentation = noop,
            RemoteMovementDiagnostics = noop,
        };
        return new LiveSessionHost(
            controller,
            new LiveSessionHostBindings(
                new LiveSessionRoutingFactories(
                    _ => new NoopEventRouting(),
                    _ => commands),
                LiveSessionResetManifest.Create(reset).Execute,
                new LiveSessionSelectionBindings(
                    id => identity.ServerGuid = id,
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
                "user",
                "password",
                AwaitCharacterSelection: awaitCharacterSelection));
    }

    private static RuntimeOptions LiveOptions()
    {
        var environment = new Dictionary<string, string?>
        {
            ["ACDREAM_LIVE"] = "1",
            ["ACDREAM_TEST_HOST"] = "127.0.0.1",
            ["ACDREAM_TEST_PORT"] = "9000",
            ["ACDREAM_TEST_USER"] = "user",
            ["ACDREAM_TEST_PASS"] = "password",
        };
        return RuntimeOptions.Parse("dat", environment.GetValueOrDefault);
    }

    private static WorldSession.EntitySpawn Spawn(
        uint guid,
        ushort instance,
        uint cell)
    {
        var position = new CreateObject.ServerPosition(
            cell,
            10f,
            10f,
            5f,
            1f,
            0f,
            0f,
            0f);
        var timestamps = new PhysicsTimestamps(
            Position: 1,
            Movement: 1,
            State: 1,
            Vector: 1,
            Teleport: 0,
            ServerControlledMove: 1,
            ForcePosition: 0,
            ObjDesc: 1,
            Instance: instance);
        var physics = new PhysicsSpawnData(
            RawState: (uint)PhysicsStateFlags.ReportCollisions,
            Position: position,
            Movement: null,
            AnimationFrame: null,
            SetupTableId: 0x02000001u,
            MotionTableId: 0x09000001u,
            SoundTableId: null,
            PhysicsScriptTableId: null,
            Parent: null,
            Children: null,
            Scale: null,
            Friction: null,
            Elasticity: null,
            Translucency: null,
            Velocity: null,
            Acceleration: null,
            AngularVelocity: null,
            DefaultScriptType: null,
            DefaultScriptIntensity: null,
            Timestamps: timestamps);
        return new WorldSession.EntitySpawn(
            guid,
            position,
            0x02000001u,
            Array.Empty<CreateObject.AnimPartChange>(),
            Array.Empty<CreateObject.TextureChange>(),
            Array.Empty<CreateObject.SubPaletteSwap>(),
            null,
            null,
            "runtime fixture",
            null,
            null,
            0x09000001u,
            PhysicsState: (uint)PhysicsStateFlags.ReportCollisions,
            InstanceSequence: instance,
            MovementSequence: 1,
            ServerControlSequence: 1,
            PositionSequence: 1,
            Physics: physics);
    }

    private sealed class SessionOperations(TestTransport transport)
        : ILiveSessionOperations
    {
        public IPEndPoint ResolveEndpoint(string host, int port) =>
            new(IPAddress.Loopback, port);

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, transport);

        public void Connect(WorldSession session, string user, string password)
        {
        }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [
                    new CharacterList.Character(
                        0x50000001u,
                        "Unavailable",
                        30u),
                    new CharacterList.Character(
                        Harness.PlayerGuid,
                        "Runtime",
                        0u),
                ],
                [],
                11,
                "Runtime",
                true,
                true);

        public void StartCharacterSelectionReceive(WorldSession session)
        {
        }

        public void EnterWorld(WorldSession session, int activeCharacterIndex)
        {
        }

        public void Tick(WorldSession session)
        {
        }

        public void DisposeSession(WorldSession session) => session.Dispose();
    }

    internal sealed class TestTransport : IWorldSessionTransport
    {
        public List<byte[]> Sent { get; } = [];

        public void Send(ReadOnlySpan<byte> datagram)
        {
            Sent.Add(datagram.ToArray());
        }

        public void Send(IPEndPoint remote, ReadOnlySpan<byte> datagram)
        {
            Sent.Add(datagram.ToArray());
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
            throw new OperationCanceledException(cancellationToken);

        public void Dispose()
        {
        }
    }

    private sealed class NoopEventRouting : ILiveSessionEventRouting
    {
        public void Attach()
        {
        }

        public void Dispose()
        {
        }
    }

    internal sealed class RecordingCommandRouting
        : ILiveSessionCommandRouting,
          ICommandBus
    {
        private bool _active;

        public List<object> Published { get; } = [];

        public void Activate() => _active = true;

        public void Publish<T>(T command) where T : notnull
        {
            if (_active)
                Published.Add(command);
        }

        public void Dispose() => _active = false;
    }

    internal sealed class RecordingCombatModeOperations
        : IRuntimeCombatModeOperations
    {
        public List<AcDream.Core.Combat.CombatMode> Sent { get; } = [];
        public bool IsInWorld => true;
        public IReadOnlyList<ClientObject> GetOrderedEquipment() => [];
        public void NotifyExplicitCombatModeRequest() { }
        public void SendChangeCombatMode(
            AcDream.Core.Combat.CombatMode mode) => Sent.Add(mode);
    }

    private sealed class NoopCombatInput : ICombatInputFrameController
    {
        public void Tick()
        {
        }

        public void HandleMovementInput(
            InputAction action,
            ActivationType activation)
        {
        }

        public void AbortAutomaticAttack()
        {
        }

        public bool HandleInputAction(
            InputAction action,
            ActivationType activation) => false;
    }

    private sealed class EntityObjectTrace : IRuntimeEntityObjectObserver
    {
        public List<string> Entries { get; } = [];

        public void OnEntity(in RuntimeEntityDelta delta) =>
            Entries.Add(
                $"E:{delta.Stamp.Sequence}:{delta.Change}:"
                + $"{delta.Entity.Identity.ServerGuid:X8}:"
                + $"{delta.Entity.Identity.LocalEntityId}:"
                + $"{delta.Entity.Identity.Incarnation}:"
                + $"{delta.Entity.CellId:X8}:"
                + $"{delta.Entity.PhysicsState:X8}:"
                + $"{delta.Entity.Position?.ObjCellId:X8}:"
                + $"{delta.Entity.Position?.Frame.Origin.X}:"
                + $"{delta.Entity.Position?.Frame.Origin.Y}:"
                + $"{delta.Entity.Position?.Frame.Origin.Z}");

        public void OnInventory(in RuntimeInventoryDelta delta) =>
            Entries.Add(
                $"I:{delta.Stamp.Sequence}:{delta.Change}:"
                + $"{delta.Item.ObjectId:X8}:"
                + $"{delta.Item.Incarnation}:"
                + $"{delta.Item.ContainerId:X8}:"
                + $"{delta.Item.ContainerSlot}:"
                + $"{delta.Item.StackSize}:"
                + $"{delta.Item.Value}");
    }

    private sealed class ThrowingEntityObserver : IRuntimeEventObserver
    {
        public void OnLifecycle(in RuntimeLifecycleDelta delta)
        {
        }

        public void OnCommand(in RuntimeCommandDelta delta)
        {
        }

        public void OnEntity(in RuntimeEntityDelta delta) =>
            throw new InvalidOperationException("observer");

        public void OnInventory(in RuntimeInventoryDelta delta)
        {
        }

        public void OnChat(in RuntimeChatDelta delta)
        {
        }

        public void OnMovement(in RuntimeMovementDelta delta)
        {
        }

        public void OnPortal(in RuntimePortalDelta delta)
        {
        }

        public void OnCombat(in RuntimeCombatDelta delta)
        {
        }
    }

    private sealed class SelectionQuery(uint target) : IWorldSelectionQuery
    {
        public uint? PickAtCursor(bool includeSelf) => target;
        public uint? PickAt(float mouseX, float mouseY, bool includeSelf) => target;
        public void BeginLightingPulse(uint serverGuid)
        {
        }

        public bool TryCaptureIdentity(uint serverGuid, out uint localEntityId)
        {
            localEntityId = 1u;
            return serverGuid == target;
        }

        public bool IsCurrent(uint serverGuid, uint localEntityId) =>
            serverGuid == target && localEntityId == 1u;

        public string Describe(uint serverGuid) => "Runtime target";
        public bool IsCreature(uint serverGuid) => serverGuid == target;
        public bool IsHostileMonster(uint serverGuid) => serverGuid == target;
        public bool IsAttackableTarget(uint serverGuid) => serverGuid == target;
        public ClosestCombatTarget? FindClosestHostileMonster() =>
            new(target, DistanceSquared: 4f);
        public bool IsUseable(uint serverGuid) => serverGuid == target;
        public bool IsPickupable(uint serverGuid) => false;
        public bool IsStuckInWorld(uint serverGuid) => false;
        public bool IsWieldedByPlayer(uint serverGuid) => false;
        public bool IsWieldedPositionState(uint serverGuid) => false;

        public bool TryGetApproach(
            uint serverGuid,
            out InteractionApproach approach)
        {
            approach = default;
            return false;
        }

        public Vector3? GetCombatCameraTargetPoint(uint serverGuid) => null;
    }

    private sealed class SelectionTransport(Func<bool> isInWorld)
        : IRuntimeInteractionTransport
    {
        public bool IsInWorld => isInWorld();

        public bool TrySendUse(uint serverGuid, out uint sequence)
        {
            sequence = 1u;
            return IsInWorld;
        }

        public bool TrySendPickup(
            uint itemGuid,
            uint destinationContainerId,
            int placement,
            out uint sequence)
        {
            sequence = 1u;
            return IsInWorld;
        }
    }

    private sealed class NoopInteractionMovement
        : IPlayerInteractionMovementSink
    {
        public bool BeginApproach(
            InteractionApproach approach,
            Action<PlayerApproachToken>? armAfterCancel = null) => false;
    }

    private sealed class NoopEntityResources
        : ILiveEntityResourceLifecycle
    {
        public void Register(WorldEntity entity)
        {
        }

        public void Unregister(WorldEntity entity)
        {
        }
    }
}
