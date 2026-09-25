using AcDream.Runtime.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Net;
using AcDream.Core.Net.Messages;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Entities;
using AcDream.Runtime.Gameplay;
using System.Numerics;

namespace AcDream.Runtime.Tests.Plugins;

public sealed class RuntimeAutomationSurfaceTests
{
    [Fact]
    public void ProjectileDebugSamplesAreDetachedValidatedAndClearedOnUnbind()
    {
        using var surface = new RuntimeAutomationSurface();
        PluginProjectileDebugSample[] source =
        [
            new(new Vector3(1f, 2f, 3f), true, 0.4f),
            new(new Vector3(float.NaN, 0f, 0f), false, 0.4f),
        ];

        surface.Projectiles.ShowDebugSamples(source);
        source[0] = default;

        PluginProjectileDebugSample sample = Assert.Single(
            surface.CaptureProjectileDebugSamples());
        Assert.Equal(new Vector3(1f, 2f, 3f), sample.WorldPosition);
        Assert.True(sample.IsClear);

        surface.Unbind();
        Assert.Empty(surface.CaptureProjectileDebugSamples());
    }

    [Fact]
    public void SelectionAutomationUsesTheBoundCanonicalActionRoute()
    {
        using var surface = new RuntimeAutomationSurface();
        var actions = new List<PluginSelectionAction>();
        surface.BindSelectionActions(action =>
        {
            actions.Add(action);
            return true;
        });

        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.PreviousSelection));
        Assert.True(surface.Selection.Execute(
            PluginSelectionAction.NextPlayer));
        Assert.Equal(
            [
                PluginSelectionAction.PreviousSelection,
                PluginSelectionAction.NextPlayer,
            ],
            actions);
    }

    [Theory]
    [InlineData((uint)ItemType.MeleeWeapon, 0u, PluginObjectClass.MeleeWeapon)]
    [InlineData((uint)ItemType.Armor, 0u, PluginObjectClass.Armor)]
    [InlineData((uint)ItemType.Creature, 0x10u, PluginObjectClass.Monster)]
    [InlineData((uint)ItemType.Creature, 0u, PluginObjectClass.Npc)]
    [InlineData((uint)ItemType.Creature, 0x04000010u, PluginObjectClass.CombatPet)]
    [InlineData((uint)ItemType.Creature, 0x8u, PluginObjectClass.Player)]
    [InlineData((uint)ItemType.Misc, 0x200u, PluginObjectClass.Vendor)]
    [InlineData((uint)ItemType.Misc, 0x1000u, PluginObjectClass.Door)]
    public void ObjectClassProjectionMatchesVirindiPriority(
        uint itemType,
        uint publicFlags,
        PluginObjectClass expected)
    {
        var item = new ClientObject
        {
            ObjectId = 1u,
            Type = (ItemType)itemType,
            PublicWeenieBitfield = publicFlags,
        };

        Assert.Equal(expected, RuntimeAutomationSurface.ClassifyObject(item));
    }

    [Theory]
    [InlineData(PluginObjectClass.Portal, PluginObjectCapabilities.Interactable | PluginObjectCapabilities.Portal)]
    [InlineData(PluginObjectClass.Door, PluginObjectCapabilities.Interactable | PluginObjectCapabilities.Door)]
    [InlineData(PluginObjectClass.Vendor, PluginObjectCapabilities.Interactable | PluginObjectCapabilities.Vendor)]
    [InlineData(PluginObjectClass.Npc, PluginObjectCapabilities.Interactable | PluginObjectCapabilities.Npc)]
    [InlineData(PluginObjectClass.Lifestone, PluginObjectCapabilities.Interactable)]
    [InlineData(PluginObjectClass.Services, PluginObjectCapabilities.Interactable)]
    [InlineData(PluginObjectClass.Corpse, PluginObjectCapabilities.Interactable)]
    [InlineData(PluginObjectClass.MeleeWeapon, PluginObjectCapabilities.None)]
    public void ObjectCapabilitiesAreStableSemanticFlags(
        PluginObjectClass objectClass,
        PluginObjectCapabilities expected)
    {
        Assert.Equal(expected, PluginObjectClassifier.Capabilities(objectClass));
    }

    [Theory]
    [InlineData((uint)ItemType.MeleeWeapon, 0u)]
    [InlineData((uint)ItemType.Armor, 0u)]
    [InlineData((uint)ItemType.Creature, 0x10u)]
    [InlineData((uint)ItemType.Creature, 0u)]
    [InlineData((uint)ItemType.Creature, 0x04000010u)]
    [InlineData((uint)ItemType.Creature, 0x8u)]
    [InlineData((uint)ItemType.Misc, 0x200u)]
    [InlineData((uint)ItemType.Misc, 0x1000u)]
    [InlineData((uint)ItemType.Writable, 0x2u)]
    [InlineData((uint)ItemType.Writable, 0x4u)]
    [InlineData((uint)ItemType.Writable, 0x1u)]
    public void ClassifyObjectDelegatesToTheSharedClassifierPlusTheScrollRule(
        uint itemType,
        uint publicFlags)
    {
        var item = new ClientObject
        {
            ObjectId = 1u,
            Type = (ItemType)itemType,
            PublicWeenieBitfield = publicFlags,
        };

        PluginObjectClass expected = PluginObjectClassifier.Classify(itemType, publicFlags);
        Assert.Equal(expected, RuntimeAutomationSurface.ClassifyObject(item));

        // The one rule the shared classifier cannot express: a written
        // object carrying an appraised spell id is a scroll, regardless of
        // what the shared classifier alone would have said.
        var scroll = new ClientObject
        {
            ObjectId = 2u,
            Type = ItemType.Writable,
            PublicWeenieBitfield = 0u,
            SpellId = 42u,
        };
        Assert.Equal(PluginObjectClass.Scroll, RuntimeAutomationSurface.ClassifyObject(scroll));
    }

    [Fact]
    public void NavigationProjectionUsesVtankMapCoordinatesAndCompassHeading()
    {
        PluginNavigationPosition center =
            RuntimeAutomationSurface.ProjectNavigationPosition(new Position(
                0x7F7F0001u,
                new Vector3(84f, 84f, 240f),
                Quaternion.Identity));

        Assert.Equal(0d, center.EastWest, 8);
        Assert.Equal(0d, center.NorthSouth, 8);
        Assert.Equal(1d, center.Elevation, 8);
        Assert.Equal(0f, center.HeadingDegrees, 4);
        Assert.True(center.IsOutdoor);

        PluginNavigationPosition nextBlock =
            RuntimeAutomationSurface.ProjectNavigationPosition(new Position(
                0x80800041u,
                new Vector3(84f, 84f, 0f),
                Quaternion.Identity));

        Assert.Equal(0.8d, nextBlock.EastWest, 8);
        Assert.Equal(0.8d, nextBlock.NorthSouth, 8);
        Assert.False(nextBlock.IsOutdoor);
    }

    [Fact]
    public void AllegianceSnapshotIsUnavailableBeforeAuthoritativeProfile()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        PluginAllegianceSnapshot snapshot = surface.Allegiance.Snapshot;
        Assert.False(snapshot.IsKnown);
        Assert.Equal(0L, snapshot.Revision);
        Assert.False(snapshot.HasMonarch);
    }

    [Fact]
    public void PortalTransitionCoalescesDuplicateRuntimeSnapshots()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        var events = new AcDream.Core.Plugins.WorldEvents();
        using var surface = new RuntimeAutomationSurface(events);
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var seen = new List<PluginPortalTransition>();
        events.PortalTransition += seen.Add;
        var snapshot = RuntimePortalSnapshot.Idle with
        {
            Generation = 3,
            Kind = RuntimePortalKind.Portal,
        };
        var observer = (IRuntimeEventObserver)surface;

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
    public void ChatCapture_isOrderedCursorBasedAndDetachesAcrossSessions()
    {
        using var first = GameRuntimeTestFactory.Create();
        using var second = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(
            first,
            first.CharacterOwner,
            first.ActionOwner.SpellCast);

        first.CommunicationOwner.AddText(
            "You cast Imperil Other VII on Olthoi.",
            RetailLogTextType.Magic);
        PluginChatMessage one = Assert.Single(surface.CaptureMessages(0));
        Assert.Equal("You cast Imperil Other VII on Olthoi.", one.Text);
        Assert.Empty(surface.CaptureMessages(one.Sequence));

        surface.Bind(
            second,
            second.CharacterOwner,
            second.ActionOwner.SpellCast);
        first.CommunicationOwner.AddText(
            "stale first-session line",
            RetailLogTextType.Magic);
        second.CommunicationOwner.AddText(
            "You cast Fester Other VII on Olthoi.",
            RetailLogTextType.Magic);

        PluginChatMessage two = Assert.Single(
            surface.CaptureMessages(one.Sequence));
        Assert.True(two.Sequence > one.Sequence);
        Assert.Equal("You cast Fester Other VII on Olthoi.", two.Text);
        // Rebinding leaves nothing behind on the session it left, while the
        // new one carries the surface plus the runtime event bridge the
        // surface's lifecycle subscription brings with it.
        Assert.Equal(0, first.CommunicationOwner.SubscriberCount);
        Assert.Equal(2, second.CommunicationOwner.SubscriberCount);

        surface.Dispose();
        Assert.Equal(0, second.CommunicationOwner.SubscriberCount);
    }

    [Fact]
    public void PostSystemMessage_RoutesToChatLog_NeverSpewBox()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.PostSystemMessage("Tank: buffs applied.");

        var entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("Tank: buffs applied.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        runtime.CommunicationOwner.SpewBox.Tick(0d);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void InventoryCompletionProjectsTheCanonicalRequestReceipt()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        ClientObjectTable objects = runtime.InventoryOwner.Objects;
        const uint itemId = 0x50000123u;
        objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Stack",
            StackSize = 10,
            StackSizeMax = 100,
        });

        Assert.True(runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Merge,
            itemId,
            static () => true));
        Assert.True(objects.UpdateStackSize(itemId, 9, 0));

        PluginInventoryCompletion completion =
            surface.Items.LastInventoryCompletion;
        Assert.True(completion.Revision > 0);
        Assert.Equal(PluginInventoryCommandKind.Merge, completion.Kind);
        Assert.Equal(itemId, completion.SourceObjectId);
        Assert.True(completion.IsSuccess);
    }

    /// <summary>
    /// Mutation executed: project <c>false</c> instead of the inventory
    /// transaction's pending-request flag in <c>CaptureBusyState</c>; the
    /// first assertion fails while the request is outstanding.
    /// </summary>
    [Fact]
    public void RecoveryProjectsDispatchedInventoryUntilMatchingAuthoritativeMove()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        const uint itemId = 0x50000124u;
        runtime.InventoryOwner.Objects.AddOrUpdate(new ClientObject
        {
            ObjectId = itemId,
            Name = "Stack",
            StackSize = 10,
            StackSizeMax = 100,
        });

        Assert.True(runtime.InventoryOwner.Transactions.TryDispatch(
            InventoryRequestKind.Move, itemId, static () => true));
        PluginBusyState pending = surface.Recovery.CaptureBusyState();
        Assert.True(pending.PendingInventory);
        Assert.Equal(0, pending.BusyCount);

        Assert.True(runtime.InventoryOwner.Objects.ApplyConfirmedServerMove(
            itemId, 0x50000001u, newWielderId: 0u));
        Assert.False(surface.Recovery.CaptureBusyState().PendingInventory);
    }

    [Fact]
    public void RecoveryClearsExactlyOneCanonicalBusyReference()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        runtime.InventoryOwner.Transactions.IncrementBusyCount();
        runtime.InventoryOwner.Transactions.IncrementBusyCount();

        PluginRecoveryResult first = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult second = surface.Recovery.ClearOneBusyReference();
        PluginRecoveryResult alreadyClear =
            surface.Recovery.ClearOneBusyReference();

        Assert.True(first.Accepted);
        Assert.Equal((2, 1), (first.PreviousCount, first.CurrentCount));
        Assert.Equal((1, 0), (second.PreviousCount, second.CurrentCount));
        Assert.Equal((0, 0),
            (alreadyClear.PreviousCount, alreadyClear.CurrentCount));
        Assert.Equal(0, runtime.InventoryOwner.Transactions.BusyCount);
    }

    [Fact]
    public void EnchantmentLedgerSharesReportedAndConfirmedLocalDurationCasts()
    {
        var operations = new SpellOperations();
        using var runtime = GameRuntimeTestFactory.Create(spellCast: operations);
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create([DurationSpell()]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(42u);
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(surface.Enchantments.ReportCast(100u, 42u, 30d));
        PluginTrackedEnchantment reported = Assert.Single(
            surface.Enchantments.Capture(100u));
        Assert.Equal(7u, reported.Family);
        Assert.Equal(350, reported.Quality);
        Assert.InRange(reported.SecondsRemaining, 29d, 30d);

        runtime.ActionOwner.Selection.Select(
            200u,
            SelectionChangeSource.Plugin);
        Assert.Equal(
            CastRequestResult.Sent,
            runtime.ActionOwner.SpellCast.Cast(42u));
        Assert.True(runtime.ActionOwner.SpellCast.CompleteUse(0u));

        Assert.True(surface.Magic.LastCompletion.IsSuccess);
        PluginTrackedEnchantment local = Assert.Single(
            surface.Enchantments.Capture(200u));
        Assert.Equal(42u, local.SpellId);
        Assert.InRange(local.SecondsRemaining, 59d, 60d);

        surface.Unbind();
        Assert.Empty(surface.Enchantments.Capture(100u));
        Assert.Empty(surface.Enchantments.Capture(200u));
    }

    [Fact]
    public void KnownSelfBuffsLeaveOutWhatTheCharacterCannotBeATargetOf()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create(
        [
            // Beneficial and self-targeted: a self buff.
            TimedBeneficial(10u, "Strength Self I", flags: 0x0000000Cu, targetMask: 0x10u),
            // Beneficial but creature-targeted with no self bit: the retail
            // target rule refuses it on the caster, so it is not a self buff
            // however good it sounds.
            TimedBeneficial(11u, "Assassin's Alchemy Kit", flags: 0x00000006u, targetMask: 0x10u),
            // Beneficial, targeted, and allowed on the caster by its mask.
            TimedBeneficial(12u, "Blessing of Someone", flags: 0x00000006u, targetMask: 0x8107u),
        ]));
        runtime.CharacterOwner.Spellbook.OnSpellLearned(10u);
        runtime.CharacterOwner.Spellbook.OnSpellLearned(11u);
        runtime.CharacterOwner.Spellbook.OnSpellLearned(12u);
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.Equal(
            [10u, 12u],
            surface.Spells.KnownSelfBuffs.Select(spell => spell.SpellId).Order());
        Assert.True(surface.Spells.TryGet(11u, out _));
    }

    [Fact]
    public void ASpellCarriesItsEffectsFormulaVersionDisplayOrderAndComponentLoss()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        runtime.CharacterOwner.InstallSpellMetadata(SpellTable.Create(
        [
            DurationSpell() with
            {
                CasterEffect = 0x2Au,
                TargetEffect = 0x3Bu,
                SortKey = 812,
                FormulaVersion = 2u,
                ComponentLoss = 0.35f,
            },
        ]));
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        Assert.True(surface.Spells.TryGet(42u, out PluginSpellInfo spell));
        Assert.Equal(0x2Au, spell.CasterEffect);
        Assert.Equal(0x3Bu, spell.TargetEffect);
        Assert.Equal(812, spell.DisplayOrder);
        Assert.Equal(2u, spell.FormulaVersion);
        Assert.Equal(0.35f, spell.ComponentLoss);
        PluginSpellInfo listed = Assert.Single(surface.Spells.All);
        Assert.Equal(spell, listed);
    }

    private static SpellMetadata TimedBeneficial(
        uint id, string name, uint flags, uint targetMask) => new(
        id,
        name,
        "Creature Enchantment",
        id,
        0u,
        string.Empty,
        60f,
        10,
        false,
        false,
        string.Empty,
        0,
        50,
        flags,
        1,
        false,
        false,
        false,
        0f,
        0u,
        0u,
        targetMask,
        1);

    private static SpellMetadata DurationSpell() => new(
        42u,
        "Fire Vulnerability Other VII",
        "Life Magic",
        7u,
        0u,
        string.Empty,
        60f,
        10,
        true,
        false,
        string.Empty,
        0,
        350,
        0u,
        7,
        false,
        true,
        false,
        0f,
        0u,
        0u,
        1u,
        0);

    private sealed class SpellOperations : IRuntimeSpellCastOperations
    {
        public uint LocalPlayerId => 1u;
        public bool CanSend => true;
        public bool HasRequiredComponents(uint spellId) => true;
        public bool IsTargetCompatible(
            uint targetId,
            SpellMetadata spell,
            bool showMessage) => true;
        public void StopCompletely() { }
        public void SendUntargeted(uint spellId) { }
        public void SendTargeted(uint targetId, uint spellId) { }
        public void DisplayMessage(string message) { }
        public void IncrementBusy() { }
    }

    [Fact]
    public void ProjectWorldObject_FillsIconIdFromTheClientObject()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 0x50000456u,
            Name = "Test Item",
            IconId = 0x06000165u,
        };

        System.Reflection.MethodInfo method = typeof(RuntimeAutomationSurface)
            .GetMethod(
                "ProjectWorldObject",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "RuntimeAutomationSurface.ProjectWorldObject was not found by reflection.");
        var result = (PluginWorldObject)method.Invoke(
            surface,
            [runtime, null, item, 0u])!;

        Assert.Equal(0x06000165u, result.IconId);
    }

    [Fact]
    public void ProjectInventoryItem_CarriesTheUseRadiusTheServerSent()
    {
        const uint lever = 0x50000322u;
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        _ = runtime.EntityObjects
            .RegisterEntity(new WorldSession.EntitySpawn(
                lever,
                null,
                null,
                [],
                [],
                [],
                null,
                null,
                "Lever",
                null,
                null,
                null,
                UseRadius: 2.5f))
            .Canonical!;
        var item = new ClientObject { ObjectId = lever, Name = "Lever" };

        PluginInventoryItem projected = surface.ProjectInventoryItem(runtime, item);
        PluginInventoryItem unknown = surface.ProjectInventoryItem(
            runtime,
            new ClientObject { ObjectId = 0x50000323u, Name = "Elsewhere" });

        Assert.Equal(2.5f, projected.UseRadius);
        Assert.Equal(0f, unknown.UseRadius);
    }

    [Fact]
    public void ProjectInventoryItem_SamplesTheColourInTheMiddleOfEachSwappedRange()
    {
        // A swap starting at block 3 (colour 24) and covering 4 blocks
        // (32 colours) is sampled at colour 24 + 16 = 40, which is where the
        // loot tools' byte position 4*16 + 3*32 + 8 = 168 lands in the file.
        const uint painted = 0x50000324u;
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);
        var colours = new IndexEchoPalette();
        surface.BindPaletteColorResolver(colours);
        _ = runtime.EntityObjects
            .RegisterEntity(new WorldSession.EntitySpawn(
                painted,
                null,
                null,
                [],
                [],
                [new CreateObject.SubPaletteSwap(0x04000123u, 3, 4)],
                null,
                null,
                "Painted",
                null,
                null,
                null))
            .Canonical!;

        PluginInventoryItem projected = surface.ProjectInventoryItem(
            runtime,
            new ClientObject { ObjectId = painted, Name = "Painted" });

        PluginPaletteInfo swap = Assert.Single(projected.Palettes);
        Assert.Equal(40, colours.LastIndex);
        Assert.Equal((byte)40, swap.Red);
    }

    /// <summary>Answers every colour with its own index in the red channel.</summary>
    private sealed class IndexEchoPalette : AcDream.Core.CharGen.IChargenPaletteColorSource
    {
        public int LastIndex { get; private set; } = -1;

        public bool TryGetColor(
            uint paletteId,
            int index,
            out AcDream.Core.CharGen.ChargenSwatchRgb color)
        {
            LastIndex = index;
            color = new AcDream.Core.CharGen.ChargenSwatchRgb((byte)index, 0, 0);
            return true;
        }
    }

    [Fact]
    public void ProjectWorldObject_PrefersThePhysicsBodyPositionOnTheGraphicalHost()
    {
        const uint remote = 0x50000321u;
        const uint cell = 0x01010100u;
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        RuntimeEntityRecord record = runtime.EntityObjects
            .RegisterEntity(new WorldSession.EntitySpawn(
                remote,
                new CreateObject.ServerPosition(cell, 51f, 10f, 5f, 1f, 0f, 0f, 0f),
                null,
                [],
                [],
                [],
                null,
                null,
                "Remote",
                null,
                null,
                null))
            .Canonical!;
        var body = new PhysicsBody { Position = new Vector3(1f, 10f, 5f) };
        body.SnapToCell(cell, body.Position, body.Position);
        runtime.EntityObjects.Entities.SetPhysicsBody(record, body);

        System.Reflection.MethodInfo method = typeof(RuntimeAutomationSurface)
            .GetMethod(
                "ProjectWorldObject",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "RuntimeAutomationSurface.ProjectWorldObject was not found by reflection.");
        var result = (PluginWorldObject)method.Invoke(
            surface,
            [runtime, record, null, 0u])!;

        PluginNavigationPosition bodyPosition = RuntimeAutomationSurface.ProjectNavigationPosition(
            new Position(cell, new Vector3(1f, 10f, 5f), Quaternion.Identity));
        Assert.Equal(0d, result.Position.HorizontalDistanceMeters(bodyPosition), 3);
    }


    [Fact]
    public void FaceHeading_IsUnavailableOnAnUnboundSurface()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            surface.Navigation.FaceHeading(90f));
    }

    /// <summary>
    /// The inert surface every host without a live local player falls back
    /// to must refuse rather than pretend.
    /// </summary>
    [Fact]
    public void FaceHeading_IsUnavailableOnTheNoOpSurface()
    {
        INavigationAutomation navigation =
            NoOpAutomationSurface.Instance.Navigation;

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            navigation.FaceHeading(90f));
    }

    /// <summary>
    /// The graphical surface hands out the runtime's navigation, which every host shares,
    /// and that implementation carries out a turn rather than falling back to the default.
    /// </summary>
    [Fact]
    public void FaceHeading_IsImplementedByTheGraphicalSurface()
    {
        using var surface = new RuntimeAutomationSurface();
        Type navigation = surface.Navigation.GetType();
        System.Reflection.InterfaceMapping map =
            navigation.GetInterfaceMap(typeof(INavigationAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            static method => method.Name
                == nameof(INavigationAutomation.FaceHeading));

        Assert.True(index >= 0, "INavigationAutomation.FaceHeading not found.");
        Assert.Equal(
            typeof(AcDream.Runtime.Navigation.RuntimeNavigationAutomation),
            navigation);
        Assert.Equal(navigation, map.TargetMethods[index].DeclaringType);
    }

    /// <summary>
    /// The room check a summoner makes is the runtime's own, shared by every
    /// host, not the interface default that always answers unknown; with no
    /// session in the world it answers unknown.
    /// </summary>
    [Fact]
    public void CheckRoomAhead_IsImplementedByTheRuntimeAndUnknownOutOfTheWorld()
    {
        using var surface = new RuntimeAutomationSurface();
        Type navigation = surface.Navigation.GetType();
        System.Reflection.InterfaceMapping map =
            navigation.GetInterfaceMap(typeof(INavigationAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            static method => method.Name
                == nameof(INavigationAutomation.CheckRoomAhead));

        Assert.True(index >= 0, "INavigationAutomation.CheckRoomAhead not found.");
        Assert.Equal(navigation, map.TargetMethods[index].DeclaringType);
        Assert.Equal(
            PluginRoomAheadStatus.Unknown,
            surface.Navigation.CheckRoomAhead(3f).Status);
    }

    [Fact]
    public void ProjectInventoryItem_preferstTheRetainedWeaponProfileOverThePropertyTable()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 903u,
            Name = "Test Sword",
            WeaponProfile = new ClientWeaponProfile(
                DamageType: 4u,
                WeaponTime: 30u,
                WeaponSkill: 34u,
                Damage: 25u,
                DamageVariance: 0.3d,
                DamageMod: 1.0d,
                WeaponLength: 1.0d,
                MaxVelocity: 2.0d,
                WeaponOffense: 1.05d,
                MaxVelocityEstimated: 1u),
        };
        // The property table carries a stale/never-sent value that the
        // retained profile must take priority over.
        item.Properties.Ints[(uint)PropertyInt.Damage] = 1;
        item.Properties.Ints[(uint)PropertyInt.WeaponSkill] = 2;
        item.Properties.Ints[(uint)PropertyInt.DamageType] = 3;
        item.Properties.Floats[(uint)PropertyFloat.DamageVariance] = 0.9d;

        PluginInventoryItem projected = surface.ProjectInventoryItem(runtime, item);

        Assert.Equal(25, projected.Damage);
        Assert.Equal(34, projected.WeaponSkill);
        Assert.Equal(4, projected.DamageType);
        Assert.Equal(0.3d, projected.DamageVariance);
    }

    [Fact]
    public void ProjectInventoryItem_fallsBackToThePropertyTableWithoutAWeaponProfile()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 904u,
            Name = "Unappraised Sword",
        };
        item.Properties.Ints[(uint)PropertyInt.Damage] = 7;
        item.Properties.Ints[(uint)PropertyInt.WeaponSkill] = 8;
        item.Properties.Ints[(uint)PropertyInt.DamageType] = 9;
        item.Properties.Floats[(uint)PropertyFloat.DamageVariance] = 0.1d;

        PluginInventoryItem projected = surface.ProjectInventoryItem(runtime, item);

        Assert.Equal(7, projected.Damage);
        Assert.Equal(8, projected.WeaponSkill);
        Assert.Equal(9, projected.DamageType);
        Assert.Equal(0.1d, projected.DamageVariance);
    }

    [Fact]
    public void RequestLogout_IsUnavailableOnAnUnboundSurface()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.False(surface.Login.CanRequestLogout);
        Assert.False(surface.Login.RequestLogout());
    }

    /// <summary>
    /// A plugin compiled without the new members, or a host that never calls
    /// BindLogout, still gets a refusal from the interface defaults.
    /// </summary>
    [Fact]
    public void RequestLogout_IsUnavailableOnTheNoOpSurface()
    {
        ILoginAutomation login = NoOpAutomationSurface.Instance.Login;

        Assert.False(login.CanRequestLogout);
        Assert.False(login.RequestLogout());
    }

    [Fact]
    public void RequestLogout_IsImplementedByTheGraphicalSurface()
    {
        System.Reflection.InterfaceMapping map =
            typeof(RuntimeAutomationSurface).GetInterfaceMap(
                typeof(ILoginAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            static method => method.Name
                == nameof(ILoginAutomation.RequestLogout));

        Assert.True(index >= 0, "ILoginAutomation.RequestLogout not found.");
        Assert.Equal(
            typeof(RuntimeAutomationSurface),
            map.TargetMethods[index].DeclaringType);
    }

    [Fact]
    public void BindLogout_RequestLogoutStaysRefusedWhileTheSurfaceIsUnavailableEvenIfBound()
    {
        using var surface = new RuntimeAutomationSurface();
        bool requested = false;
        surface.BindLogout(
            () => { requested = true; return true; },
            () => true);

        Assert.False(surface.Login.CanRequestLogout);
        Assert.False(surface.Login.RequestLogout());
        Assert.False(requested);
    }

    [Fact]
    public void DropGiveAndApplyRefuseAsUnavailableBeforeBindItems()
    {
        using var surface = new RuntimeAutomationSurface();

        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            surface.Items.Drop(0x50000001u).Status);
        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            surface.Items.Give(0x50000001u, 0x50000002u).Status);
        Assert.Equal(
            PluginItemCommandStatus.Unavailable,
            surface.Items.Apply(0x50000001u, 0x50000002u).Status);
    }

    [Fact]
    public void OwnedItemProjectionCarriesTheSingularNameForAStack()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new RuntimeAutomationSurface();
        var stack = new ClientObject
        {
            ObjectId = 0x50000777u,
            Name = "Lead Scarab",
            PluralName = "Lead Scarabs",
            StackSize = 5,
            StackSizeMax = 100,
        };

        PluginInventoryItem item = surface.ProjectInventoryItem(runtime, stack);

        Assert.Equal("Lead Scarabs", stack.GetAppropriateName());
        Assert.Equal("Lead Scarab", item.Name);
        Assert.Equal(5, item.StackSize);
    }
}
