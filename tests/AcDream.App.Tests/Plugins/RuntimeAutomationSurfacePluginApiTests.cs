using AcDream.App.Interaction;
using AcDream.App.Plugins;
using AcDream.Runtime.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Combat;
using AcDream.Core.Plugins;
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
            pickupItem: (_, _) => false,
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
            pickupItem: (_, _) => false,
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
            pickupItem: (_, _) => false,
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
            pickupItem: (_, _) => false,
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
    private static (GameRuntime Runtime, DirectGameRuntimeCommandAdapter Commands) CreateRealSession()
    {
        var operations = new RealSessionOperations();
        GameRuntime runtime = GameRuntimeTestFactory.Create(session: operations);
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

        public WorldSession CreateSession(IPEndPoint endpoint) =>
            new(endpoint, new NoOpTransport());

        public void Connect(WorldSession session, string user, string password) { }

        public CharacterList.Parsed GetCharacters(WorldSession session) =>
            new(
                0u,
                [new CharacterList.Character(0x50000001u, "PluginApiFixture", 0u)],
                [],
                11,
                "PluginApi",
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
