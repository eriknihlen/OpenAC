using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Social;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using AcDream.Runtime;

namespace AcDream.Runtime.Tests.Gameplay;

public sealed class RuntimeGameplayOwnershipTests
{
    [Fact]
    public void GameplayOwnershipLedgerConvergesEntityPhysicsAndGameplayAsOneLifetime()
    {
        var entities = new RuntimeEntityObjectLifetime();
        var inventory = new RuntimeInventoryState(entities);
        var character = new RuntimeCharacterState();
        var communication = new RuntimeCommunicationState();
        var actions = RuntimeActionTestFactory.Create(inventory.Transactions);
        var movement = new RuntimeLocalPlayerMovementState();
        var fellowship = new RuntimeFellowshipState();
        var allegiance = new RuntimeAllegianceState();
        var trade = new RuntimeTradeState();

        movement.Execute(RuntimeMovementCommand.ToggleRunLock);
        inventory.Transactions.IncrementBusyCount();
        actions.Selection.Select(
            0x70000001u,
            AcDream.Core.Selection.SelectionChangeSource.System);
        character.Spellbook.OnSpellLearned(42u);
        communication.Chat.OnSystemMessage("runtime-only", 0u);

        RuntimeSimulationOwnershipSnapshot populated =
            RuntimeSimulationOwnership.Capture(
                entities,
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade);
        Assert.False(populated.IsConverged);

        movement.Dispose();
        actions.Dispose();
        communication.Dispose();
        character.Dispose();
        inventory.Dispose();
        fellowship.Dispose();
        allegiance.Dispose();
        trade.Dispose();
        entities.Dispose();

        RuntimeSimulationOwnershipSnapshot retired =
            RuntimeSimulationOwnership.Capture(
                entities,
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade);
        Assert.True(retired.IsConverged);
        Assert.True(retired.EntityObjects.IsDisposed);
        Assert.True(retired.Physics.IsDisposed);
    }

    [Fact]
    public void CombinedLedgerConvergesEveryGameplayOwnerAndSubscription()
    {
        using var entities = new RuntimeEntityObjectLifetime();
        var inventory = new RuntimeInventoryState(entities);
        var character = new RuntimeCharacterState();
        var communication = new RuntimeCommunicationState();
        var actions = RuntimeActionTestFactory.Create(inventory.Transactions);
        var movement = new RuntimeLocalPlayerMovementState();
        var fellowship = new RuntimeFellowshipState();
        var allegiance = new RuntimeAllegianceState();
        var trade = new RuntimeTradeState();
        movement.Execute(RuntimeMovementCommand.ToggleRunLock);
        inventory.Shortcuts.Changed += static () => { };
        inventory.Shortcuts.Load([new ShortcutEntry(1, 2u, 3u)]);
        inventory.ItemMana.OnQueryItemManaResponse(2u, 0.5f, true);
        inventory.ExternalContainers.RequestOpen(3u);
        inventory.ExternalContainers.ApplyViewContents(3u);
        inventory.Transactions.IncrementBusyCount();

        character.Spellbook.OnSpellLearned(4u);
        character.Spellbook.SetFavorite(0, 0, 4u);
        character.Spellbook.SetDesiredComponent(5u, 6u);
        character.LocalPlayer.OnVitalUpdate(7u, 1u, 2u, 3u, 4u);
        character.LocalPlayer.OnAttributeUpdate(1u, 2u, 3u, 4u);
        character.LocalPlayer.OnSkillUpdate(
            6u, 1u, 2u, 3u, 4u, 5u, 6d, 7u);
        character.Options.Replace(1u, 2u);
        character.MovementSkills.Update(3, 4);

        communication.Chat.OnTellReceived(
            "Sender",
            "hello",
            0x50000001u,
            logTextType: 0x03u);
        communication.Chat.OnSelfSent(
            ChatKind.Tell,
            "reply",
            logTextType: 0x04u,
            targetOrChannel: "Recipient");
        communication.TurbineChat.OnChannelsReceived(
            1u, 2u, 3u, 4u, 5u, 6u, 7u, 8u, 9u, 10u);
        communication.Friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Full,
            [new FriendEntry(1u, "Friend", true, false, [], [])]));
        communication.Squelch.Replace(new SquelchDatabase(
            new Dictionary<string, uint> { ["account"] = 1u },
            new Dictionary<uint, SquelchInfo>
            {
                [2u] = new SquelchInfo(
                    "Character",
                    false,
                    new HashSet<uint> { 3u }),
            },
            new SquelchInfo(
                string.Empty,
                false,
                new HashSet<uint> { 4u })));
        IDisposable subscription =
            communication.Events.Subscribe(new NoopObserver());
        actions.Selection.Select(
            0x50000002u,
            AcDream.Core.Selection.SelectionChangeSource.World);
        actions.Combat.SetCombatMode(AcDream.Core.Combat.CombatMode.Melee);
        actions.Combat.OnUpdateHealth(0x50000002u, 0.75f);
        actions.Interaction.EnterUse();

        RuntimeGameplayOwnershipSnapshot populated =
            RuntimeGameplayOwnership.Capture(
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade);

        Assert.False(populated.IsConverged);
        Assert.Equal(1, populated.Inventory.ShortcutCount);
        Assert.Equal(1, populated.Character.LearnedSpellCount);
        Assert.Equal(1, populated.Communication.StreamSubscriberCount);
        Assert.True(populated.Communication.HasReplyTarget);
        Assert.True(populated.Communication.HasRetellTarget);
        Assert.Equal(0x50000002u, populated.Actions.SelectedObjectId);
        Assert.Equal(
            AcDream.Core.Combat.CombatMode.Melee,
            populated.Actions.CombatMode);
        Assert.Equal(1, populated.Actions.TrackedTargetHealthCount);
        Assert.True(populated.Movement.AutoRunActive);

        movement.Dispose();
        actions.Dispose();
        communication.Dispose();
        character.Dispose();
        inventory.Dispose();
        fellowship.Dispose();
        allegiance.Dispose();
        trade.Dispose();
        subscription.Dispose();

        RuntimeGameplayOwnershipSnapshot retired =
            RuntimeGameplayOwnership.Capture(
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade);

        Assert.True(retired.IsConverged);
        Assert.Equal(0, retired.Inventory.ShortcutSubscriberCount);
        Assert.False(retired.Character.InternalSubscriptionsAttached);
        Assert.Equal(0, retired.Communication.StreamSubscriberCount);
        Assert.Equal(0, retired.Communication.PendingDispatchCount);
    }

    [Fact]
    public void BorrowerFailuresCannotStrandAnyGameplayOwnerDuringTerminalDisposal()
    {
        using var entities = new RuntimeEntityObjectLifetime();
        var inventory = new RuntimeInventoryState(entities);
        var character = new RuntimeCharacterState();
        var communication = new RuntimeCommunicationState();
        var actions = RuntimeActionTestFactory.Create(inventory.Transactions);
        var movement = new RuntimeLocalPlayerMovementState();
        var fellowship = new RuntimeFellowshipState();
        var allegiance = new RuntimeAllegianceState();
        var trade = new RuntimeTradeState();

        inventory.ExternalContainers.RequestOpen(0x70000001u);
        inventory.ExternalContainers.ApplyViewContents(0x70000001u);
        inventory.ExternalContainers.Changed += static _ =>
            throw new InvalidOperationException("inventory observer");

        character.Spellbook.OnSpellLearned(42u);
        character.LocalPlayer.OnVitalUpdate(7u, 1u, 2u, 3u, 4u);
        character.Spellbook.SpellbookChanged += static () =>
            throw new InvalidOperationException("spellbook observer");
        character.LocalPlayer.CharacterChanged += static () =>
            throw new InvalidOperationException("character observer");

        _ = communication.Events.Subscribe(new ThrowingObserver());
        communication.Chat.OnSystemMessage("failure-isolated event", 0u);
        Assert.Equal(1, communication.DispatchFailureCount);

        movement.Dispose();
        actions.Dispose();
        Assert.Throws<AggregateException>(inventory.Dispose);
        Assert.Throws<AggregateException>(character.Dispose);
        communication.Dispose();
        fellowship.Dispose();
        allegiance.Dispose();
        trade.Dispose();

        RuntimeGameplayOwnershipSnapshot retired =
            RuntimeGameplayOwnership.Capture(
                inventory,
                character,
                communication,
                actions,
                movement,
                fellowship,
                allegiance,
                trade);

        Assert.True(retired.IsConverged);
        Assert.Equal(1, retired.Communication.DispatchFailureCount);
    }

    private sealed class NoopObserver : IRuntimeCommunicationObserver
    {
        public void OnChat(in RuntimeCommunicationEvent delta) { }
    }

    private sealed class ThrowingObserver : IRuntimeCommunicationObserver
    {
        public void OnChat(in RuntimeCommunicationEvent delta) =>
            throw new InvalidOperationException("communication observer");
    }
}
