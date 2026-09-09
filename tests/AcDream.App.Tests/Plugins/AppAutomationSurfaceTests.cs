using AcDream.App.Plugins;
using AcDream.Core.Chat;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Selection;
using AcDream.Core.Spells;
using AcDream.Plugin.Abstractions;
using AcDream.Runtime.Gameplay;
using System.Numerics;

namespace AcDream.App.Tests.Plugins;

public sealed class AppAutomationSurfaceTests
{
    [Fact]
    public void ProjectileDebugSamplesAreDetachedValidatedAndClearedOnUnbind()
    {
        using var surface = new AppAutomationSurface();
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
        using var surface = new AppAutomationSurface();
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

        Assert.Equal(expected, AppAutomationSurface.ClassifyObject(item));
    }

    [Fact]
    public void NavigationProjectionUsesVtankMapCoordinatesAndCompassHeading()
    {
        PluginNavigationPosition center =
            AppAutomationSurface.ProjectNavigationPosition(new Position(
                0x7F7F0001u,
                new Vector3(84f, 84f, 240f),
                Quaternion.Identity));

        Assert.Equal(0d, center.EastWest, 8);
        Assert.Equal(0d, center.NorthSouth, 8);
        Assert.Equal(1d, center.Elevation, 8);
        Assert.Equal(0f, center.HeadingDegrees, 4);
        Assert.True(center.IsOutdoor);

        PluginNavigationPosition nextBlock =
            AppAutomationSurface.ProjectNavigationPosition(new Position(
                0x80800041u,
                new Vector3(84f, 84f, 0f),
                Quaternion.Identity));

        Assert.Equal(0.8d, nextBlock.EastWest, 8);
        Assert.Equal(0.8d, nextBlock.NorthSouth, 8);
        Assert.False(nextBlock.IsOutdoor);
    }

    [Fact]
    public void ChatCapture_isOrderedCursorBasedAndDetachesAcrossSessions()
    {
        using var first = GameRuntimeTestFactory.Create();
        using var second = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
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
        Assert.Equal(1, second.CommunicationOwner.SubscriberCount);

        surface.Dispose();
        Assert.Equal(0, second.CommunicationOwner.SubscriberCount);
    }

    [Fact]
    public void PostSystemMessage_RoutesToChatLog_NeverSpewBox()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
        surface.Bind(runtime, runtime.CharacterOwner, runtime.ActionOwner.SpellCast);

        surface.PostSystemMessage("MossTank: buffs applied.");

        var entry = Assert.Single(runtime.CommunicationOwner.Chat.Snapshot());
        Assert.Equal("MossTank: buffs applied.", entry.Text);
        Assert.Equal((uint)RetailLogTextType.Default, entry.LogTextType);

        runtime.CommunicationOwner.SpewBox.Tick(0d);
        Assert.Equal(0, runtime.CommunicationOwner.SpewBox.Count);
    }

    [Fact]
    public void InventoryCompletionProjectsTheCanonicalRequestReceipt()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
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

    [Fact]
    public void RecoveryClearsExactlyOneCanonicalBusyReference()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
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
        using var surface = new AppAutomationSurface();
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
        using var surface = new AppAutomationSurface();
        var item = new ClientObject
        {
            ObjectId = 0x50000456u,
            Name = "Test Item",
            IconId = 0x06000165u,
        };

        System.Reflection.MethodInfo method = typeof(AppAutomationSurface)
            .GetMethod(
                "ProjectWorldObject",
                System.Reflection.BindingFlags.NonPublic
                    | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "AppAutomationSurface.ProjectWorldObject was not found by reflection.");
        var result = (PluginWorldObject)method.Invoke(
            surface,
            [runtime, null, item, 0u])!;

        Assert.Equal(0x06000165u, result.IconId);
    }


    [Fact]
    public void FaceHeading_IsUnavailableOnAnUnboundSurface()
    {
        using var surface = new AppAutomationSurface();

        Assert.Equal(
            PluginNavigationCommandStatus.Unavailable,
            surface.Navigation.FaceHeading(90f));
    }

    /// <summary>
    /// The inert surface every host without a live local player falls back
    /// to — including <c>HeadlessAutomationSurface</c>, which has no
    /// navigation surface of its own — must refuse rather than pretend.
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

    [Fact]
    public void FaceHeading_IsImplementedByTheGraphicalSurface()
    {
        System.Reflection.InterfaceMapping map =
            typeof(AppAutomationSurface).GetInterfaceMap(
                typeof(INavigationAutomation));
        int index = Array.FindIndex(
            map.InterfaceMethods,
            static method => method.Name
                == nameof(INavigationAutomation.FaceHeading));

        Assert.True(index >= 0, "INavigationAutomation.FaceHeading not found.");
        Assert.Equal(
            typeof(AppAutomationSurface),
            map.TargetMethods[index].DeclaringType);
    }

    [Fact]
    public void OwnedItemProjectionCarriesTheSingularNameForAStack()
    {
        using var runtime = GameRuntimeTestFactory.Create();
        using var surface = new AppAutomationSurface();
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
