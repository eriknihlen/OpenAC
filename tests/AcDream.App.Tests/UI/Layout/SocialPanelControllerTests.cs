using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Core.Social;
using AcDream.Runtime;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SocialPanelControllerTests
{
    private static readonly RuntimeCommandResult InactiveResult =
        new(RuntimeCommandStatus.Inactive, default);
    private static readonly RuntimeCommandResult AcceptedResult =
        new(RuntimeCommandStatus.Accepted, default);

    private static SocialFellowshipPageController.Bindings MakeFellowshipBindings(
        List<string>? calls = null,
        RuntimeFellowshipSnapshot snapshot = default,
        IEnumerable<RuntimeFellowMemberSnapshot>? members = null,
        SelectionState? selection = null,
        uint localPlayerGuid = 0u,
        Func<CharacterOptionId, bool>? currentCharacterOption = null,
        Func<uint, uint, UiElement?>? templateResolver = null,
        Func<uint, uint, string?>? resolveString = null,
        Func<bool>? panelOpenInWorld = null)
    {
        calls ??= new List<string>();
        Func<bool> inWorld = panelOpenInWorld ?? (static () => true);
        return new SocialFellowshipPageController.Bindings(
            Snapshot: () => snapshot,
            Members: () => members ?? [],
            TemplateResolver: templateResolver ?? FakeRowTemplateResolver,
            Create: (name, shareXp) => { calls.Add($"fellowship-create:{name}:{shareXp}"); return InactiveResult; },
            Recruit: guid => { calls.Add($"fellowship-recruit:{guid:X8}"); return InactiveResult; },
            Dismiss: guid => { calls.Add($"fellowship-dismiss:{guid:X8}"); return InactiveResult; },
            Quit: disband => { calls.Add($"fellowship-quit:{disband}"); return InactiveResult; },
            AssignLeader: guid => { calls.Add($"fellowship-assign-leader:{guid:X8}"); return InactiveResult; },
            SetOpen: isOpen => { calls.Add($"fellowship-set-open:{isOpen}"); return InactiveResult; },
            SetPanelOpen: panelOpen => { calls.Add($"fellowship-set-panel-open:{panelOpen}"); return inWorld() ? AcceptedResult : InactiveResult; },
            Selection: selection ?? new SelectionState(),
            LocalPlayerGuid: () => localPlayerGuid,
            CurrentCharacterOption: currentCharacterOption ?? (_ => false),
            SetCharacterOption: (id, value) => calls.Add($"fellowship-set-option:{id}:{value}"),
            ResolveString: resolveString ?? ((_, _) => null));
    }

    private static SocialAllegiancePageController.Bindings MakeAllegianceBindings(
        List<string>? calls = null,
        RuntimeAllegianceSnapshot snapshot = default,
        RuntimeAllegianceMemberSnapshot? monarch = null,
        Func<uint, RuntimeAllegianceMemberSnapshot?>? patron = null,
        Func<uint, RuntimeAllegianceMemberSnapshot?>? member = null,
        Func<uint, IEnumerable<RuntimeAllegianceMemberSnapshot>>? vassals = null,
        AcDream.Core.Selection.SelectionState? selection = null,
        uint localPlayerGuid = 0u,
        Func<CharacterOptionId, bool>? currentCharacterOption = null,
        Func<uint, uint, UiElement?>? templateResolver = null,
        Func<uint, uint, string?>? resolveString = null,
        Func<uint, string?>? resolveWorldObjectName = null,
        Func<string, Action<bool>, uint>? showConfirmation = null)
    {
        calls ??= new List<string>();
        return new SocialAllegiancePageController.Bindings(
            Snapshot: () => snapshot,
            Monarch: () => monarch,
            Patron: patron ?? (_ => null),
            Member: member ?? (_ => null),
            Vassals: vassals ?? (_ => []),
            Swear: guid => { calls.Add($"allegiance-swear:{guid:X8}"); return InactiveResult; },
            Break: guid => { calls.Add($"allegiance-break:{guid:X8}"); return InactiveResult; },
            Kick: guid => { calls.Add($"allegiance-kick:{guid:X8}"); return InactiveResult; },
            SetUpdateSubscription: on => { calls.Add($"allegiance-set-subscription:{on}"); return AcceptedResult; },
            Selection: selection ?? new AcDream.Core.Selection.SelectionState(),
            LocalPlayerGuid: () => localPlayerGuid,
            CurrentCharacterOption: currentCharacterOption ?? (_ => false),
            SetCharacterOption: (id, value) => calls.Add($"allegiance-set-option:{id}:{value}"),
            TemplateResolver: templateResolver ?? FakeRowTemplateResolver,
            ResolveString: resolveString ?? ((_, _) => null),
            ResolveWorldObjectName: resolveWorldObjectName ?? (_ => null),
            ShowConfirmation: showConfirmation ?? ((_, _) => 0u));
    }

    private static SocialPanelController.Callbacks MakeCallbacks(
        List<string>? calls = null,
        RuntimeFellowshipSnapshot fellowship = default,
        RuntimeAllegianceSnapshot allegiance = default,
        FriendsState? friends = null,
        SquelchState? squelch = null,
        Func<bool>? panelOpenInWorld = null,
        SocialAllegiancePageController.Bindings? allegianceBindings = null)
    {
        calls ??= new List<string>();
        return new SocialPanelController.Callbacks(
            Toggle: () => calls.Add("toggle"),
            Fellowship: MakeFellowshipBindings(calls, fellowship, panelOpenInWorld: panelOpenInWorld),
            Allegiance: allegianceBindings ?? MakeAllegianceBindings(calls, allegiance),
            Friends: friends ?? new FriendsState(),
            Squelch: squelch ?? new SquelchState(),
            TemplateResolver: FakeRowTemplateResolver);
    }

    private static UiElement? FakeRowTemplateResolver(uint layoutId, uint elementId)
    {
        var outer = new UiText { Width = 270f, Height = 24f };
        var inner = new UiText();
        outer.AddChild(inner);
        return outer;
    }


    [Fact]
    public void Bind_RootBuildsAsUiTabPanel()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();

        Assert.IsType<UiTabPanel>(layout.Root);
    }

    [Fact]
    public void TabTable_MatchesLiveDatPairing_AllegianceIsDefault()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        var tabs = Assert.IsType<UiTabPanel>(layout.Root);

        Assert.Equal(4, tabs.Tabs.Count);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x1000028Cu && e.PageElementId == 0x10000291u && e.IsDefault);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x1000028Eu && e.PageElementId == 0x10000292u && !e.IsDefault);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x10000512u && e.PageElementId == 0x10000513u && !e.IsDefault);
        Assert.Contains(tabs.Tabs, e =>
            e.ButtonElementId == 0x1000053Bu && e.PageElementId == 0x1000054Au && !e.IsDefault);
        Assert.Single(tabs.Tabs, e => e.IsDefault);
    }

    [Fact]
    public void Bind_Succeeds_AndActivateTabs_SelectsTheAuthoredDefault()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();

        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks());

        Assert.NotNull(controller);
        controller!.ActivateTabs();
        Assert.Empty(controller.TabPanel.UnresolvedEntries);
        Assert.True(controller.IsShowingAllegiance);
        Assert.False(controller.IsShowingFellowship);
    }

    [Fact]
    public void ShowFellowship_SwitchesTheActiveTab()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks());
        Assert.NotNull(controller);
        controller!.ActivateTabs();

        controller.ShowFellowship();

        Assert.True(controller.IsShowingFellowship);
        Assert.False(controller.IsShowingAllegiance);
    }

    [Fact]
    public void CloseButton_InvokesToggleCallback()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        var calls = new List<string>();
        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks(calls));
        Assert.NotNull(controller);

        var close = Assert.IsType<UiButton>(layout.FindElement(0x10000290u));
        close.OnEvent(new UiEvent(0, close, UiEventType.Click));

        Assert.Contains("toggle", calls);
    }

    // ── Fellowship empty state (item 4) ─────────────────────────────────────

    [Fact]
    public void Fellowship_NoFellowship_ShowsNotInFellowshipFrame()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = false }));
        Assert.NotNull(controller);

        UiElement notIn = UiElement.FindDescendant(controller!.TabPanel, 0x1000026Bu)!;
        UiElement inFellowship = UiElement.FindDescendant(controller.TabPanel, 0x10000275u)!;
        Assert.True(notIn.Visible);
        Assert.False(inFellowship.Visible);
    }

    [Fact]
    public void Fellowship_HasFellowship_ShowsInFellowshipFrame()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = true }));
        Assert.NotNull(controller);

        UiElement notIn = UiElement.FindDescendant(controller!.TabPanel, 0x1000026Bu)!;
        UiElement inFellowship = UiElement.FindDescendant(controller.TabPanel, 0x10000275u)!;
        Assert.False(notIn.Visible);
        Assert.True(inFellowship.Visible);
    }


    [Fact]
    public void Allegiance_NoProfile_HidesBlocksAndBlanksNames()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(allegiance: new RuntimeAllegianceSnapshot { HasProfile = false }));
        Assert.NotNull(controller);

        UiElement monarchField = UiElement.FindDescendant(controller!.TabPanel, 0x10000255u)!;
        UiElement patronField = UiElement.FindDescendant(controller.TabPanel, 0x1000025Au)!;
        Assert.False(monarchField.Visible);
        Assert.False(patronField.Visible);

        var monarchName = Assert.IsType<UiText>(
            UiElement.FindDescendant(monarchField, 0x10000257u));
        var patronName = Assert.IsType<UiText>(
            UiElement.FindDescendant(patronField, 0x1000025Cu));
        Assert.Equal(" ", Assert.Single(monarchName.LinesProvider()).Text);
        Assert.Equal(" ", Assert.Single(patronName.LinesProvider()).Text);
    }

    [Fact]
    public void Allegiance_HasMonarch_NotSelf_ShowsMonarchBlock_AndRendersData()
    {
        const uint selfGuid = 100u;
        const uint monarchGuid = 200u;
        var monarch = new RuntimeAllegianceMemberSnapshot(
            monarchGuid, 0u, true, "Queen Alice", 0, 0, 0, 0, 0u, 0u, 0, 0, false);
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true, TotalMembers = 3u },
            monarch: monarch,
            localPlayerGuid: selfGuid);

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement monarchField = UiElement.FindDescendant(controller!.TabPanel, 0x10000255u)!;
        Assert.True(monarchField.Visible);
        var monarchName = Assert.IsType<UiText>(UiElement.FindDescendant(monarchField, 0x10000257u));
        Assert.Equal("Queen Alice", Assert.Single(monarchName.LinesProvider()).Text);
        var monarchFollowers = Assert.IsType<UiText>(UiElement.FindDescendant(monarchField, 0x10000258u));
        Assert.Equal("Followers: 2", Assert.Single(monarchFollowers.LinesProvider()).Text);
    }

    [Fact]
    public void Allegiance_IsMonarch_HidesMonarchBlock()
    {
        const uint selfGuid = 100u;
        var monarch = new RuntimeAllegianceMemberSnapshot(
            selfGuid, 0u, true, "Me", 0, 0, 0, 0, 0u, 0u, 0, 0, false);
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true },
            monarch: monarch,
            localPlayerGuid: selfGuid);

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement monarchField = UiElement.FindDescendant(controller!.TabPanel, 0x10000255u)!;
        Assert.False(monarchField.Visible);
    }

    /// <summary>Patron analogue of the monarch test — a real patron who is
    /// NOT the monarch shows the block with the patron's name.</summary>
    [Fact]
    public void Allegiance_HasPatron_NotMonarch_ShowsPatronBlock_AndRendersName()
    {
        const uint selfGuid = 100u;
        const uint patronGuid = 300u;
        var patron = new RuntimeAllegianceMemberSnapshot(
            patronGuid, 0u, true, "Sir Bob", 0, 0, 0, 0, 0u, 0u, 0, 0, false);
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true },
            patron: guid => guid == selfGuid ? patron : null,
            localPlayerGuid: selfGuid);

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement patronField = UiElement.FindDescendant(controller!.TabPanel, 0x1000025Au)!;
        Assert.True(patronField.Visible);
        var patronName = Assert.IsType<UiText>(UiElement.FindDescendant(patronField, 0x1000025Cu));
        Assert.Equal("Sir Bob", Assert.Single(patronName.LinesProvider()).Text);
    }

    [Fact]
    public void Allegiance_PatronIsMonarch_HidesPatronBlock_RevealsMonarchSubBlock()
    {
        const uint selfGuid = 100u;
        const uint monarchGuid = 200u;
        var monarch = new RuntimeAllegianceMemberSnapshot(
            monarchGuid, 0u, true, "Queen Alice", 0, 0, 0, 0, 0u, 0u, 0, 0, false);
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true, TotalMembers = 2u },
            monarch: monarch,
            patron: guid => guid == selfGuid ? monarch : null,
            localPlayerGuid: selfGuid);

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement monarchField = UiElement.FindDescendant(controller!.TabPanel, 0x10000255u)!;
        UiElement patronField = UiElement.FindDescendant(controller.TabPanel, 0x1000025Au)!;
        Assert.True(monarchField.Visible);
        Assert.False(patronField.Visible);

        UiElement subBlock = UiElement.FindDescendant(monarchField, 0x10000490u)!;
        Assert.True(subBlock.Visible);
    }

    [Fact]
    public void Allegiance_VassalList_PopulatesOneRowPerVassal()
    {
        const uint selfGuid = 100u;
        var vassals = new List<RuntimeAllegianceMemberSnapshot>
        {
            new(401u, selfGuid, true, "Vassal One", 0, 0, 0, 0, 0u, 10u, 0, 0, true),
            new(402u, selfGuid, false, "Vassal Two", 0, 0, 0, 0, 0u, 20u, 0, 0, true),
        };
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true, Revision = 1 },
            vassals: guid => guid == selfGuid ? vassals : [],
            localPlayerGuid: selfGuid);

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement allegiancePage = UiElement.FindDescendant(controller!.TabPanel, 0x10000291u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(allegiancePage, 0x10000260u));
        Assert.Equal(2, listBox.ViewportForTest!.Children.Count);
    }

    [Fact]
    public void Allegiance_OfflineCue_IsTheMarkerOnly_NameStaysWhite()
    {
        const uint selfGuid = 100u;
        var vassals = new List<RuntimeAllegianceMemberSnapshot>
        {
            new(401u, selfGuid, true, "Online Vassal", 0, 0, 0, 0, 0u, 0u, 0, 0, true),
            new(402u, selfGuid, false, "Offline Vassal", 0, 0, 0, 0, 0u, 0u, 0, 0, true),
        };

        static UiElement? TaggedRowResolver(uint layoutId, uint elementId)
        {
            var row = new UiPanel();
            var name = new UiText { DatElementId = 0x10000268u };
            var marker = new UiPanel { DatElementId = 0x100004AAu };
            row.AddChild(name);
            row.AddChild(marker);
            return row;
        }

        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true, Revision = 1 },
            vassals: guid => guid == selfGuid ? vassals : [],
            localPlayerGuid: selfGuid,
            templateResolver: TaggedRowResolver);
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement page = UiElement.FindDescendant(controller!.TabPanel, 0x10000291u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(page, 0x10000260u));
        var rows = listBox.ViewportForTest!.Children;
        Assert.Equal(2, rows.Count);

        foreach (UiElement row in rows)
        {
            var name = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000268u));
            UiElement marker = UiElement.FindDescendant(row, 0x100004AAu)!;
            bool online = name.LinesProvider()[0].Text == "Online Vassal";

            // The marker IS the offline cue: shown iff offline.
            Assert.Equal(!online, marker.Visible);
            // The name is always white — no invented offline grey.
            Assert.Equal(Vector4.One, name.LinesProvider()[0].Color);
        }
    }

    [Fact]
    public void Allegiance_Swear_ShowsConfirmation_AndSendsOnAccept()
    {
        const uint selfGuid = 100u;
        const uint targetGuid = 500u;
        var calls = new List<string>();
        Action<bool>? capturedCallback = null;
        var selection = new AcDream.Core.Selection.SelectionState();
        selection.Select(targetGuid, AcDream.Core.Selection.SelectionChangeSource.World);
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            calls,
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true },
            selection: selection,
            localPlayerGuid: selfGuid,
            resolveWorldObjectName: guid => guid == targetGuid ? "Target Player" : null,
            showConfirmation: (message, completed) =>
            {
                calls.Add($"confirm:{message}");
                capturedCallback = completed;
                return 1u;
            });

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(calls, allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement allegiancePage = UiElement.FindDescendant(controller!.TabPanel, 0x10000291u)!;
        var swearButton = Assert.IsType<UiButton>(UiElement.FindDescendant(allegiancePage, 0x10000263u));
        swearButton.OnClick!();

        Assert.Contains("confirm:Target Player", calls);
        Assert.NotNull(capturedCallback);
        capturedCallback!(true);
        Assert.Contains($"allegiance-swear:{targetGuid:X8}", calls);
    }

    [Fact]
    public void Allegiance_Break_TargetsCurrentPatron()
    {
        const uint selfGuid = 100u;
        const uint patronGuid = 300u;
        var patron = new RuntimeAllegianceMemberSnapshot(
            patronGuid, 0u, true, "Sir Bob", 0, 0, 0, 0, 0u, 0u, 0, 0, false);
        var calls = new List<string>();
        Action<bool>? capturedCallback = null;
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            calls,
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true },
            patron: guid => guid == selfGuid ? patron : null,
            localPlayerGuid: selfGuid,
            showConfirmation: (message, completed) =>
            {
                calls.Add($"confirm:{message}");
                capturedCallback = completed;
                return 1u;
            });

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(calls, allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement allegiancePage = UiElement.FindDescendant(controller!.TabPanel, 0x10000291u)!;
        var breakButton = Assert.IsType<UiButton>(UiElement.FindDescendant(allegiancePage, 0x10000264u));
        breakButton.OnClick!();

        Assert.NotNull(capturedCallback);
        capturedCallback!(true);
        Assert.Contains($"allegiance-break:{patronGuid:X8}", calls);
    }

    [Fact]
    public void Allegiance_Kick_TargetsSelectedVassalRow()
    {
        const uint selfGuid = 100u;
        const uint vassalGuid = 401u;
        var vassal = new RuntimeAllegianceMemberSnapshot(
            vassalGuid, selfGuid, true, "Vassal One", 0, 0, 0, 0, 0u, 0u, 0, 0, true);
        var calls = new List<string>();
        Action<bool>? capturedCallback = null;
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        UiElement? TaggedVassalRowResolver(uint layoutId, uint elementId)
        {
            var row = new UiPanel();
            var name = new UiText();
            name.DatElementId = 0x10000268u;
            row.AddChild(name);
            return row;
        }
        SocialAllegiancePageController.Bindings bindings = MakeAllegianceBindings(
            calls,
            snapshot: new RuntimeAllegianceSnapshot { HasProfile = true, Revision = 1 },
            vassals: guid => guid == selfGuid ? [vassal] : [],
            member: guid => guid == vassalGuid ? vassal : null,
            localPlayerGuid: selfGuid,
            templateResolver: TaggedVassalRowResolver,
            showConfirmation: (message, completed) =>
            {
                calls.Add($"confirm:{message}");
                capturedCallback = completed;
                return 1u;
            });

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(calls, allegianceBindings: bindings));
        Assert.NotNull(controller);

        UiElement allegiancePage = UiElement.FindDescendant(controller!.TabPanel, 0x10000291u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(allegiancePage, 0x10000260u));
        var kickButton = Assert.IsType<UiButton>(UiElement.FindDescendant(allegiancePage, 0x10000265u));
        Assert.False(kickButton.Enabled); // nothing selected yet

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        var rowName = Assert.IsType<UiText>(UiElement.FindDescendant(row, 0x10000268u));
        rowName.OnClick!();
        controller!.Tick();
        Assert.True(kickButton.Enabled);

        kickButton.OnClick!();
        Assert.NotNull(capturedCallback);
        capturedCallback!(true);
        Assert.Contains($"allegiance-kick:{vassalGuid:X8}", calls);
    }

    // ── CF-1: 0x001F subscription arming points ─────────────────────────────

    [Fact]
    public void AllegiancePageVisible_DeclaresSubscription_OnWindowShown_AndClearsOnHidden()
    {
        var calls = new List<string>();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(calls));
        Assert.NotNull(controller);
        controller!.ActivateTabs();
        calls.Clear();

        Assert.DoesNotContain(calls, c => c.StartsWith("allegiance-set-subscription"));

        controller.OnShown();
        Assert.Contains("allegiance-set-subscription:True", calls);

        calls.Clear();
        controller.OnHidden();
        Assert.Contains("allegiance-set-subscription:False", calls);
    }

    [Fact]
    public void Reconnect_ReDeclaresSubscription_AfterWorldEntry_EvenWhilePanelClosed()
    {
        var calls = new List<string>();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(calls));
        Assert.NotNull(controller);
        controller!.ActivateTabs();
        calls.Clear();

        controller.ResetSessionDeclaration();
        Assert.DoesNotContain(calls, c => c.StartsWith("allegiance-set-subscription"));

        controller.RedeclareAfterWorldEntry();
        Assert.Contains("allegiance-set-subscription:True", calls);
    }

    // ── Friends/Squelch read-only lists (item 5) ────────────────────────────

    [Fact]
    public void Friends_PopulatesOneRowPerEntry()
    {
        var friends = new FriendsState();
        friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Full,
            new List<FriendEntry>
            {
                new(1u, "Alice", true, false, System.Array.Empty<uint>(), System.Array.Empty<uint>()),
                new(2u, "Bob", false, false, System.Array.Empty<uint>(), System.Array.Empty<uint>()),
            }));
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(friends: friends));
        Assert.NotNull(controller);

        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(controller!.TabPanel, 0x10000513u) is { } friendsPage
                ? UiElement.FindDescendant(friendsPage, 0x10000517u)
                : null);
        Assert.Equal(2, listBox.ViewportForTest!.Children.Count);
    }

    [Fact]
    public void Friends_ReactsToRevisionChange_OnTick()
    {
        var friends = new FriendsState();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(friends: friends));
        Assert.NotNull(controller);

        controller!.OnShown();

        UiElement friendsPage = UiElement.FindDescendant(controller.TabPanel, 0x10000513u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(friendsPage, 0x10000517u));
        Assert.Equal(0, listBox.ViewportForTest?.Children.Count ?? 0);

        friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Add,
            new List<FriendEntry>
            {
                new(1u, "Alice", true, false, System.Array.Empty<uint>(), System.Array.Empty<uint>()),
            }));
        controller.Tick();

        Assert.Single(listBox.ViewportForTest!.Children);
    }

    [Fact]
    public void Squelch_PopulatesCharacterAndAccountRows()
    {
        var squelch = new SquelchState();
        squelch.Replace(new SquelchDatabase(
            new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase) { ["BadAccount"] = 1u },
            new Dictionary<uint, SquelchInfo>
            {
                [7u] = new SquelchInfo("Grief", false, new HashSet<uint>()),
            },
            new SquelchInfo(string.Empty, false, new HashSet<uint>())));
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();

        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(squelch: squelch));
        Assert.NotNull(controller);

        UiElement squelchPage = UiElement.FindDescendant(controller!.TabPanel, 0x1000054Au)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(squelchPage, 0x1000053Eu));
        Assert.Equal(2, listBox.ViewportForTest!.Children.Count);
    }

    // ── D1: Friends/Squelch action buttons are honest INERT ────────────────

    [Fact]
    public void FriendsAndSquelchActionButtons_AreClickable_ButHaveNoHandler()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks());
        Assert.NotNull(controller);

        foreach (uint buttonId in new[] { 0x10000514u, 0x10000515u, 0x10000516u, 0x1000052Cu })
        {
            var button = Assert.IsType<UiButton>(
                UiElement.FindDescendant(controller!.TabPanel, buttonId));
            Assert.Null(button.OnClick);
        }
        foreach (uint buttonId in new[] { 0x10000547u, 0x1000054Bu, 0x1000054Cu })
        {
            var button = Assert.IsType<UiButton>(
                UiElement.FindDescendant(controller!.TabPanel, buttonId));
            Assert.Null(button.OnClick);
        }
    }

    // ── Scrollbar wiring (blast MF-1) ───────────────────────────────────────

    [Fact]
    public void Friends_ScrollbarModel_IsWiredToListBoxScroll()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks());
        Assert.NotNull(controller);

        UiElement friendsPage = UiElement.FindDescendant(controller!.TabPanel, 0x10000513u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(friendsPage, 0x10000517u));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(friendsPage, 0x10000518u));

        Assert.Same(listBox.Scroll, scrollbar.Model);
    }

    [Fact]
    public void Squelch_ScrollbarModel_IsWiredToListBoxScroll()
    {
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(layout, MakeCallbacks());
        Assert.NotNull(controller);

        UiElement squelchPage = UiElement.FindDescendant(controller!.TabPanel, 0x1000054Au)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(squelchPage, 0x1000053Eu));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(squelchPage, 0x10000543u));

        Assert.Same(listBox.Scroll, scrollbar.Model);
    }

    [Fact]
    public void Friends_LongRoster_IsReachableViaScrollbar()
    {
        var friends = new FriendsState();
        var entries = new List<FriendEntry>();
        for (uint i = 0; i < 40; i++)
            entries.Add(new(i, $"Friend{i}", true, false, System.Array.Empty<uint>(), System.Array.Empty<uint>()));
        friends.Apply(new FriendsUpdate(FriendsUpdateType.Full, entries));

        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(friends: friends));
        Assert.NotNull(controller);

        UiElement friendsPage = UiElement.FindDescendant(controller!.TabPanel, 0x10000513u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(friendsPage, 0x10000517u));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(friendsPage, 0x10000518u));

        Assert.True(
            listBox.ContentHeight > (int)listBox.Height,
            $"40 rows (contentHeight={listBox.ContentHeight}) should exceed the box's own height ({listBox.Height})");

        scrollbar.Model!.SetExtents(listBox.ContentHeight, (int)listBox.Height);
        Assert.True(scrollbar.Model.HasOverflow);
        int before = scrollbar.Model.ScrollY;
        scrollbar.Model.ScrollByLines(4);
        Assert.True(scrollbar.Model.ScrollY > before);
    }

    [Fact]
    public void Squelch_LongRoster_IsReachableViaScrollbar()
    {
        var characters = new Dictionary<uint, SquelchInfo>();
        for (uint i = 0; i < 40; i++)
            characters[i] = new SquelchInfo($"Grief{i}", false, new HashSet<uint>());
        var squelch = new SquelchState();
        squelch.Replace(new SquelchDatabase(
            new Dictionary<string, uint>(System.StringComparer.OrdinalIgnoreCase),
            characters,
            new SquelchInfo(string.Empty, false, new HashSet<uint>())));

        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(squelch: squelch));
        Assert.NotNull(controller);

        UiElement squelchPage = UiElement.FindDescendant(controller!.TabPanel, 0x1000054Au)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(squelchPage, 0x1000053Eu));
        var scrollbar = Assert.IsType<UiScrollbar>(
            UiElement.FindDescendant(squelchPage, 0x10000543u));

        Assert.True(
            listBox.ContentHeight > (int)listBox.Height,
            $"40 rows (contentHeight={listBox.ContentHeight}) should exceed the box's own height ({listBox.Height})");

        scrollbar.Model!.SetExtents(listBox.ContentHeight, (int)listBox.Height);
        Assert.True(scrollbar.Model.HasOverflow);
        int before = scrollbar.Model.ScrollY;
        scrollbar.Model.ScrollByLines(4);
        Assert.True(scrollbar.Model.ScrollY > before);
    }

    // ── Rebuild discipline (blast SF-2/SF-3) ────────────────────────────────

    [Fact]
    public void Friends_RevisionBumpWhileHidden_DoesNotRebuild_ButRebuildsOnShow()
    {
        var friends = new FriendsState();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout, MakeCallbacks(friends: friends));
        Assert.NotNull(controller);

        UiElement friendsPage = UiElement.FindDescendant(controller!.TabPanel, 0x10000513u)!;
        var listBox = Assert.IsType<UiTemplateListBox>(
            UiElement.FindDescendant(friendsPage, 0x10000517u));

        friends.Apply(new FriendsUpdate(
            FriendsUpdateType.Add,
            new List<FriendEntry> { new(1u, "Alice", true, false, System.Array.Empty<uint>(), System.Array.Empty<uint>()) }));

        // Panel never shown — Tick() must not rebuild.
        controller!.Tick();
        Assert.Equal(0, listBox.ViewportForTest?.Children.Count ?? 0);

        // Showing the panel and ticking again picks up the accumulated bump.
        controller.OnShown();
        controller.Tick();
        Assert.Single(listBox.ViewportForTest!.Children);
    }


    [Fact]
    public void FellowshipPageVisible_Declares0x00A6_OnlyWhenWindowShownANDFellowshipActive()
    {
        var calls = new List<string>();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(calls, fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = true }));
        Assert.NotNull(controller);
        controller!.ActivateTabs(); // authored default tab is Allegiance, not Fellowship
        calls.Clear();

        // Window not shown yet, and not on the Fellowship tab -> no send.
        Assert.DoesNotContain(calls, c => c.StartsWith("fellowship-set-panel-open"));

        controller.OnShown();
        Assert.DoesNotContain(calls, c => c.StartsWith("fellowship-set-panel-open"));

        controller.ShowFellowship();
        Assert.Contains("fellowship-set-panel-open:True", calls);

        calls.Clear();
        controller.OnHidden();
        Assert.Contains("fellowship-set-panel-open:False", calls);
    }

    [Fact]
    public void Reconnect_ReDeclares0x00A6_AfterWorldEntry_NotDuringPreWorldReset()
    {
        var calls = new List<string>();
        bool inWorld = true;
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(
                calls,
                fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = true },
                panelOpenInWorld: () => inWorld));
        Assert.NotNull(controller);
        controller!.ActivateTabs();
        controller.OnShown();
        controller.ShowFellowship();
        Assert.Contains("fellowship-set-panel-open:True", calls); // declared in world
        calls.Clear();

        // Reconnect: the generation reset runs BEFORE the new session is in
        // world. The pre-world reset must leave NO published 0x00A6 (the
        // REOPEN bug latched a dropped send here and never retried).
        inWorld = false;
        controller.ResetSessionDeclaration();
        Assert.DoesNotContain(calls, c => c.StartsWith("fellowship-set-panel-open"));

        // World entry: the post-world seam re-declares, now Accepted.
        inWorld = true;
        controller.RedeclareAfterWorldEntry();
        Assert.Contains("fellowship-set-panel-open:True", calls);
    }

    [Fact]
    public void Reconnect_StaysSilent_WhenFellowshipPageIsNotActuallyOpen()
    {
        var calls = new List<string>();
        bool inWorld = true;
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(
                calls,
                fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = true },
                panelOpenInWorld: () => inWorld));
        Assert.NotNull(controller);
        controller!.ActivateTabs(); // default tab: Allegiance (Fellowship not active)
        controller.OnShown();
        calls.Clear();

        inWorld = false;
        controller.ResetSessionDeclaration();
        inWorld = true;
        controller.RedeclareAfterWorldEntry();

        Assert.DoesNotContain(calls, c => c.StartsWith("fellowship-set-panel-open"));
    }

    // ── SF-4: Dispose unsubscribes ActivePageChanged ────────────────────────

    [Fact]
    public void Dispose_UnsubscribesActivePageChanged_TabSwitchAfterDisposeSendsNoCommand()
    {
        var calls = new List<string>();
        ImportedLayout layout = FixtureLoader.LoadSocialPanelHost();
        SocialPanelController? controller = SocialPanelController.Bind(
            layout,
            MakeCallbacks(calls, fellowship: new RuntimeFellowshipSnapshot { IsInFellowship = true }));
        Assert.NotNull(controller);
        controller!.ActivateTabs();
        controller.OnShown();
        calls.Clear();

        controller.Dispose();

        controller.ShowFellowship();

        Assert.DoesNotContain(calls, c => c.StartsWith("fellowship-set-panel-open"));
    }
}
