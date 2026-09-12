using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Net.Messages;
using AcDream.Core.Selection;
using AcDream.Runtime;

namespace AcDream.App.Tests.UI.Layout;

public sealed class SocialFellowshipPageControllerTests
{
    private const uint NotInFellowshipFrameId = 0x1000026Bu;
    private const uint InFellowshipFrameId = 0x10000275u;
    private const uint NameEntryBoxId = 0x1000026Fu;
    private const uint CreateButtonId = 0x10000274u;
    private const uint FellowshipNameTextId = 0x10000276u;
    private const uint ListBoxId = 0x10000279u;
    private const uint ScrollbarId = 0x1000027Au;
    private const uint LeaderButtonId = 0x1000027Bu;
    private const uint QuitButtonId = 0x1000027Cu;
    private const uint OpenButtonId = 0x1000027Du;
    private const uint RecruitButtonId = 0x1000027Eu;
    private const uint DismissButtonId = 0x1000027Fu;
    private const uint DisbandButtonId = 0x10000280u;
    private const uint IgnoreCheckboxId = 0x10000270u;
    private const uint AutoAcceptCheckboxId = 0x10000271u;
    private const uint ShareXpCheckboxId = 0x10000272u;
    private const uint ShareLootCheckboxId = 0x10000273u;

    private const uint RowNameTextId = 0x10000283u;
    private const uint RowStatsTextId = 0x10000284u;
    private const uint RowHealthMeterId = 0x10000285u;
    private const uint RowStaminaMeterId = 0x10000287u;
    private const uint RowManaMeterId = 0x10000289u;

    private static readonly Func<uint, (uint, int, int)> NoTex = static _ => (0u, 0, 0);

    private static UiButton MakeButton(uint id)
    {
        var button = new UiButton(new ElementInfo { Id = id, Type = 1u }, NoTex);
        button.DatElementId = id;
        return button;
    }

    private static UiElement BuildPageRoot(out UiTemplateListBox listBox, out UiField nameField)
    {
        var root = new UiPanel();
        void AddPlain(UiElement child, uint id)
        {
            child.DatElementId = id;
            root.AddChild(child);
        }

        AddPlain(new UiPanel(), NotInFellowshipFrameId);
        AddPlain(new UiPanel(), InFellowshipFrameId);

        nameField = new UiField();
        AddPlain(nameField, NameEntryBoxId);

        AddPlain(MakeButton(CreateButtonId), CreateButtonId);
        AddPlain(new UiText(), FellowshipNameTextId);

        listBox = new UiTemplateListBox(
            new ElementInfo { Id = ListBoxId, Type = 5u },
            NoTex,
            new[] { new UiTemplateListEntry(0x21000030u, 0x10000281u) },
            scrollbarElementId: ScrollbarId);
        AddPlain(listBox, ListBoxId);

        AddPlain(new UiScrollbar(), ScrollbarId);

        AddPlain(MakeButton(LeaderButtonId), LeaderButtonId);
        AddPlain(MakeButton(QuitButtonId), QuitButtonId);
        AddPlain(MakeButton(OpenButtonId), OpenButtonId);
        AddPlain(MakeButton(RecruitButtonId), RecruitButtonId);
        AddPlain(MakeButton(DismissButtonId), DismissButtonId);
        AddPlain(MakeButton(DisbandButtonId), DisbandButtonId);
        AddPlain(MakeButton(IgnoreCheckboxId), IgnoreCheckboxId);
        AddPlain(MakeButton(AutoAcceptCheckboxId), AutoAcceptCheckboxId);
        AddPlain(MakeButton(ShareXpCheckboxId), ShareXpCheckboxId);
        AddPlain(MakeButton(ShareLootCheckboxId), ShareLootCheckboxId);

        return root;
    }

    private static UiElement BuildFakeFellowRow()
    {
        var row = new UiPanel();
        void Add(UiElement child, uint id)
        {
            child.DatElementId = id;
            row.AddChild(child);
        }
        Add(new UiText(), RowNameTextId);
        Add(new UiText(), RowStatsTextId);
        Add(new UiMeter(), RowHealthMeterId);
        Add(new UiMeter(), RowStaminaMeterId);
        Add(new UiMeter(), RowManaMeterId);
        return row;
    }

    private static int _rowBuildCount;

    private static UiElement? FakeRowResolver(uint layoutId, uint elementId)
    {
        _rowBuildCount++;
        return BuildFakeFellowRow();
    }

    private static readonly RuntimeCommandResult InactiveResult = new(RuntimeCommandStatus.Inactive, default);
    private static readonly RuntimeCommandResult AcceptedResult = new(RuntimeCommandStatus.Accepted, default);

    private sealed class FellowshipBindingsBuilder
    {
        public RuntimeFellowshipSnapshot Snapshot;
        public List<RuntimeFellowMemberSnapshot> Members = [];
        public readonly List<string> Calls = [];
        public readonly Dictionary<CharacterOptionId, bool> Options = new();
        public SelectionState Selection = new();
        public uint LocalPlayerGuid;
        public Func<uint, uint, UiElement?> TemplateResolver = FakeRowResolver;
        public Func<uint, uint, string?> ResolveString = static (_, _) => null;
        public Func<uint, uint, IReadOnlyDictionary<uint, string>, string?>? ResolveTemplate;
        public Func<uint, long>? ExperienceToRaiseLevel;
        public bool PanelOpenInWorld = true;

        public SocialFellowshipPageController.Bindings Build() => new(
            Snapshot: () => Snapshot,
            Members: () => Members,
            TemplateResolver: TemplateResolver,
            Create: (name, shareXp) => { Calls.Add($"create:{name}:{shareXp}"); return InactiveResult; },
            Recruit: guid => { Calls.Add($"recruit:{guid:X8}"); return InactiveResult; },
            Dismiss: guid => { Calls.Add($"dismiss:{guid:X8}"); return InactiveResult; },
            Quit: disband => { Calls.Add($"quit:{disband}"); return InactiveResult; },
            AssignLeader: guid => { Calls.Add($"assign-leader:{guid:X8}"); return InactiveResult; },
            SetOpen: isOpen => { Calls.Add($"set-open:{isOpen}"); return InactiveResult; },
            SetPanelOpen: panelOpen => { Calls.Add($"set-panel-open:{panelOpen}"); return PanelOpenInWorld ? AcceptedResult : InactiveResult; },
            Selection: Selection,
            LocalPlayerGuid: () => LocalPlayerGuid,
            CurrentCharacterOption: id => Options.TryGetValue(id, out bool v) && v,
            SetCharacterOption: (id, value) => { Options[id] = value; Calls.Add($"set-option:{id}:{value}"); },
            ResolveString: ResolveString,
            ResolveTemplate: ResolveTemplate,
            ExperienceToRaiseLevel: ExperienceToRaiseLevel);
    }


    [Fact]
    public void Bind_NotInFellowship_ShowsEmptyFrame()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };

        SocialFellowshipPageController? controller = SocialFellowshipPageController.Bind(root, b.Build());

        Assert.NotNull(controller);
        Assert.True(UiElement.FindDescendant(root, NotInFellowshipFrameId)!.Visible);
        Assert.False(UiElement.FindDescendant(root, InFellowshipFrameId)!.Visible);
    }

    [Fact]
    public void Bind_InFellowship_ShowsPopulatedFrame()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true } };

        SocialFellowshipPageController.Bind(root, b.Build());

        Assert.False(UiElement.FindDescendant(root, NotInFellowshipFrameId)!.Visible);
        Assert.True(UiElement.FindDescendant(root, InFellowshipFrameId)!.Visible);
    }

    // ── Roster building ──────────────────────────────────────────────────

    [Fact]
    public void Tick_BuildsOneRowPerMember_WithNameLevelAndVitals()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot
            {
                IsInFellowship = true,
                Revision = 1,
                ShareXp = true,
                EvenXpSplit = true,
                MemberCount = 1,
            },
            Members =
            [
                new RuntimeFellowMemberSnapshot(
                    Guid: 0x50000001u, Name: "Alice", Level: 12,
                    MaxHealth: 100, MaxStamina: 80, MaxMana: 60,
                    CurrentHealth: 55, CurrentStamina: 80, CurrentMana: 10,
                    ShareLoot: true),
            ],
        };

        SocialFellowshipPageController.Bind(root, b.Build());

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        UiText name = Assert.IsType<UiText>(UiElement.FindDescendant(row, RowNameTextId));
        Assert.Equal("Alice", name.LinesProvider().Single().Text);

        UiText stats = Assert.IsType<UiText>(UiElement.FindDescendant(row, RowStatsTextId));
        // 1-member even split -> 1.0 -> "100%" (lane B §7.2 table index 0).
        Assert.Equal("12/100%", stats.LinesProvider().Single().Text);

        var health = Assert.IsType<UiMeter>(UiElement.FindDescendant(row, RowHealthMeterId));
        Assert.Equal(0.55f, health.Fill()!.Value, 3);
        Assert.Equal("55/100", health.Label());
    }

    [Fact]
    public void Tick_SameMemberSet_UpdatesRowsInPlace_NoRebuild()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(
            0x50000001u, "Alice", 12, 100, 80, 60, 55, 80, 10, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1 },
            Members = [member],
        };

        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        UiElement firstRow = Assert.Single(listBox.ViewportForTest!.Children);

        b.Snapshot = b.Snapshot with { Revision = 2 };
        b.Members = [member with { CurrentHealth = 10 }];
        controller.Tick();

        UiElement onlyRow = Assert.Single(listBox.ViewportForTest!.Children);
        Assert.Same(firstRow, onlyRow); // NOT rebuilt — same row instance
        var health = Assert.IsType<UiMeter>(UiElement.FindDescendant(onlyRow, RowHealthMeterId));
        Assert.Equal(0.10f, health.Fill()!.Value, 3);
    }

    [Fact]
    public void Tick_MembershipChange_RebuildsRoster()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var alice = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1 },
            Members = [alice],
        };

        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        listBox.ViewportForTest!.ApplyAnchor(listBox.Width, listBox.Height);
        listBox.ViewportForTest!.LayoutScrollableChildren();
        listBox.Scroll.SetScrollY(0); // only one short row — nothing to scroll, but exercise the path

        var bob = new RuntimeFellowMemberSnapshot(0x50000002u, "Bob", 10, 90, 70, 50, 90, 70, 50, false);
        b.Snapshot = b.Snapshot with { Revision = 2, MemberCount = 2 };
        b.Members = [alice, bob];
        controller.Tick();

        Assert.Equal(2, listBox.ViewportForTest!.Children.Count);
    }

    [Fact]
    public void Tick_MemberLeaves_ShrinksRoster_AndClearsSelectionIfTheyWereSelected()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var alice = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var bob = new RuntimeFellowMemberSnapshot(0x50000002u, "Bob", 10, 90, 70, 50, 90, 70, 50, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot
            {
                IsInFellowship = true, Revision = 1, MemberCount = 2, LeaderGuid = 0x50000001u,
            },
            Members = [alice, bob],
            LocalPlayerGuid = 0x50000001u, // Alice, the leader
        };

        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        Assert.Equal(2, listBox.ViewportForTest!.Children.Count);

        UiElement bobRow = listBox.ViewportForTest!.Children[1];
        UiText bobName = Assert.IsType<UiText>(UiElement.FindDescendant(bobRow, RowNameTextId));
        bobName.OnClick!(); // selects Bob
        ((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).OnClick!();
        Assert.Contains("dismiss:50000002", b.Calls);
        b.Calls.Clear();

        b.Snapshot = b.Snapshot with { Revision = 2, MemberCount = 1 };
        b.Members = [alice];
        controller.Tick();

        Assert.Single(listBox.ViewportForTest!.Children);

        // The stale selection must be gone — Dismiss must not re-send
        // Bob's guid.
        ((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).OnClick!();
        Assert.DoesNotContain(b.Calls, c => c.StartsWith("dismiss:"));
    }

    [Fact]
    public void Tick_RowTemplatePermanentlyFailsToBuild_DoesNotRetryOnEveryVitalsTick()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        int resolverCalls = 0;
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1 },
            Members = [member],
            TemplateResolver = (_, _) => { resolverCalls++; return null; }, // permanently unbuildable
        };

        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        Assert.Empty(listBox.ViewportForTest?.Children ?? []);
        int callsAfterFirstAttempt = resolverCalls;
        Assert.True(callsAfterFirstAttempt >= 1);

        // Two pure vitals ticks: SAME member set, revision bumps (the exact
        // shape a 0x02C0 vitals refresh produces).
        b.Snapshot = b.Snapshot with { Revision = 2 };
        controller.Tick();
        b.Snapshot = b.Snapshot with { Revision = 3 };
        controller.Tick();

        Assert.Equal(callsAfterFirstAttempt, resolverCalls);
    }

    [Fact]
    public void RecruitButton_TargetIsAnExistingFellowWhoseRowFailedToBuild_StaysDisabled()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var selection = new SelectionState();
        selection.Select(0x50000001u, SelectionChangeSource.World); // targeting the (already-a-fellow) guid
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1, LeaderGuid = 1u },
            Members = [member],
            LocalPlayerGuid = 1u,
            Selection = selection,
            TemplateResolver = (_, _) => null, // Alice's row never builds -- but she IS still a fellow
        };

        SocialFellowshipPageController.Bind(root, b.Build());

        Assert.False(((UiButton)UiElement.FindDescendant(root, RecruitButtonId)!).Enabled);
    }

    // ── MUST-FIX 4: world→panel selection sync ──────────────────────────

    [Fact]
    public void WorldSelectionOfAFellow_EnablesDismissAndLeader_WithoutClickingTheirRow()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var selection = new SelectionState();
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1, LeaderGuid = 0xFFu },
            Members = [member],
            LocalPlayerGuid = 0xFFu, // leader
            Selection = selection,
        };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        Assert.False(((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).Enabled);
        Assert.False(((UiButton)UiElement.FindDescendant(root, LeaderButtonId)!).Enabled);

        selection.Select(0x50000001u, SelectionChangeSource.World);
        controller.Tick();

        Assert.True(((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).Enabled);
        Assert.True(((UiButton)UiElement.FindDescendant(root, LeaderButtonId)!).Enabled);
    }

    [Fact]
    public void WorldSelectionOfANonFellow_DoesNotClearAnExistingPanelSelection()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var selection = new SelectionState();
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1, LeaderGuid = 0xFFu },
            Members = [member],
            LocalPlayerGuid = 0xFFu,
            Selection = selection,
        };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        UiText name = Assert.IsType<UiText>(UiElement.FindDescendant(row, RowNameTextId));
        name.OnClick!(); // selects Alice via her row
        controller.Tick(); // Enabled is refreshed by RefreshButtonStates, which only runs in Tick
        Assert.True(((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).Enabled);

        selection.Select(0x99999999u, SelectionChangeSource.World);
        controller.Tick();

        Assert.True(((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).Enabled);
    }


    [Fact]
    public void OpenButton_Click_FlipsCaptionImmediately_NotWaitingForTheNextTick()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, IsOpen = false, LeaderGuid = 1u },
            LocalPlayerGuid = 1u,
            ResolveString = (_, hash) =>
                hash == DatStringResolver.ComputeHash("ID_Fellowship_OpenFellowshipButtonText") ? "Open"
                : hash == DatStringResolver.ComputeHash("ID_Fellowship_CloseFellowshipButtonText") ? "Close"
                : null,
        };
        SocialFellowshipPageController.Bind(root, b.Build());
        var openButton = (UiButton)UiElement.FindDescendant(root, OpenButtonId)!;
        Assert.Equal("Open", openButton.Label);

        openButton.OnClick!();

        Assert.Equal("Close", openButton.Label);
    }

    [Fact]
    public void Tick_NotInFellowship_ClearsAnyStaleRoster()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1 },
            Members = [member],
        };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        Assert.Single(listBox.ViewportForTest!.Children);

        b.Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false };
        controller.Tick();

        Assert.Empty(listBox.ViewportForTest!.Children);
    }

    // ── D5 display ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, 1, "12/0%")]
    [InlineData(true, true, 9, "12/31%")]   // even split, 9 fellows -> 0.3111111 -> truncate(31.11) = 31
    [InlineData(true, false, 3, "12/100%")] // proportional: the only fellow carries the whole share
    [InlineData(true, true, 6, "12/44%")]
    [InlineData(true, true, 8, "12/34%")]
    public void FormatStatsText_MatchesD5Rules(bool shareXp, bool evenSplit, int memberCount, string expected)
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot
            {
                IsInFellowship = true, Revision = 1, ShareXp = shareXp,
                EvenXpSplit = evenSplit, MemberCount = memberCount,
            },
            Members = [member],
            ExperienceToRaiseLevel = level => level * 1000L,
        };

        SocialFellowshipPageController.Bind(root, b.Build());

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        UiText stats = Assert.IsType<UiText>(UiElement.FindDescendant(row, RowStatsTextId));
        Assert.Equal(expected, stats.LinesProvider().Single().Text);
    }

    // OpenAC #38: the authored row is wider than the list, so its
    // right-justified stats text would end under the scrollbar; the text
    // gives up exactly that overlap on its right.
    [Fact]
    public void StatsText_GivesUpTheRowsOverlapPastTheList()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        listBox.Width = 271f;
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1 },
            Members = [member],
            TemplateResolver = (_, _) =>
            {
                UiElement row = BuildFakeFellowRow();
                row.Width = 279f;
                UiElement stats = UiElement.FindDescendant(row, RowStatsTextId)!;
                stats.Left = 199f;
                stats.Width = 80f;
                return row;
            },
        };

        SocialFellowshipPageController.Bind(root, b.Build());

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        UiElement stats = UiElement.FindDescendant(row, RowStatsTextId)!;
        Assert.Equal(199f, stats.Left);
        Assert.Equal(72f, stats.Width);
    }

    // OpenAC #38: the share of an uneven split is each fellow's next-level
    // cost over the sum of everyone's, and the text comes from the authored
    // "level/share%" template.
    [Fact]
    public void FormatStatsText_ProportionalSplit_UsesNextLevelCostsAndTheAuthoredTemplate()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var alice = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var bob = new RuntimeFellowMemberSnapshot(0x50000002u, "Bob", 20, 100, 80, 60, 100, 80, 60, false);
        var seen = new List<(uint table, uint key, string level, string percent)>();
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot
            {
                IsInFellowship = true, Revision = 1, ShareXp = true,
                EvenXpSplit = false, MemberCount = 2,
            },
            Members = [alice, bob],
            ExperienceToRaiseLevel = level => level * 100L,   // 1200 and 2000 -> 37% and 62%
            ResolveTemplate = (table, key, vars) =>
            {
                seen.Add((table, key, vars[5286556u], vars[174673717u]));
                return $"{vars[5286556u]}/{vars[174673717u]}%";
            },
        };

        SocialFellowshipPageController.Bind(root, b.Build());

        UiElement[] rows = listBox.ViewportForTest!.Children.ToArray();
        Assert.Equal("12/37%", Assert.IsType<UiText>(UiElement.FindDescendant(rows[0], RowStatsTextId)).LinesProvider().Single().Text);
        Assert.Equal("20/62%", Assert.IsType<UiText>(UiElement.FindDescendant(rows[1], RowStatsTextId)).LinesProvider().Single().Text);
        Assert.All(seen, entry => Assert.Equal((0x23000001u, 0x003B5A03u), (entry.table, entry.key)));
    }


    [Fact]
    public void CreateButton_DisabledWhileNameFieldEmpty_EnabledOnceTyped()
    {
        UiElement root = BuildPageRoot(out _, out UiField nameField);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        var createButton = (UiButton)UiElement.FindDescendant(root, CreateButtonId)!;

        Assert.False(createButton.Enabled);

        nameField.SetText("The Wanderers");
        controller.Tick();
        Assert.True(createButton.Enabled);

        nameField.SetText("   ");
        controller.Tick();
        Assert.False(createButton.Enabled);
    }

    [Fact]
    public void CreateButton_Click_SendsTypedName_AndTheShareXpOptionValue()
    {
        UiElement root = BuildPageRoot(out _, out UiField nameField);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };
        b.Options[CharacterOptionId.FellowshipShareXP] = true;
        SocialFellowshipPageController.Bind(root, b.Build());
        var createButton = (UiButton)UiElement.FindDescendant(root, CreateButtonId)!;

        nameField.SetText("The Wanderers");
        createButton.OnClick!();

        Assert.Contains("create:The Wanderers:True", b.Calls);
    }

    [Fact]
    public void CreateButton_Click_WithEmptyName_DoesNotSend()
    {
        UiElement root = BuildPageRoot(out _, out UiField nameField);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };
        SocialFellowshipPageController.Bind(root, b.Build());
        var createButton = (UiButton)UiElement.FindDescendant(root, CreateButtonId)!;

        createButton.OnClick!();

        Assert.DoesNotContain(b.Calls, c => c.StartsWith("create:"));
    }

    // ── Member action buttons ───────────────────────────────────────────

    [Fact]
    public void QuitButton_Click_SendsQuit_WithDisbandFalse()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true } };
        SocialFellowshipPageController.Bind(root, b.Build());
        ((UiButton)UiElement.FindDescendant(root, QuitButtonId)!).OnClick!();

        Assert.Contains("quit:False", b.Calls);
    }

    [Fact]
    public void DisbandButton_Click_SendsQuit_WithDisbandTrue()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true } };
        SocialFellowshipPageController.Bind(root, b.Build());
        ((UiButton)UiElement.FindDescendant(root, DisbandButtonId)!).OnClick!();

        Assert.Contains("quit:True", b.Calls);
    }

    [Fact]
    public void OpenButton_Click_TogglesTheCurrentOpenState()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, IsOpen = false, LeaderGuid = 1u },
            LocalPlayerGuid = 1u,
        };
        SocialFellowshipPageController.Bind(root, b.Build());
        ((UiButton)UiElement.FindDescendant(root, OpenButtonId)!).OnClick!();

        Assert.Contains("set-open:True", b.Calls);
    }

    [Fact]
    public void RecruitButton_Click_SendsTheCurrentWorldSelection()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var selection = new SelectionState();
        selection.Select(0x60000001u, SelectionChangeSource.World);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true },
            Selection = selection,
        };
        SocialFellowshipPageController.Bind(root, b.Build());
        ((UiButton)UiElement.FindDescendant(root, RecruitButtonId)!).OnClick!();

        Assert.Contains("recruit:60000001", b.Calls);
    }

    [Fact]
    public void DismissAndLeaderButtons_Click_TargetTheSelectedFellow()
    {
        UiElement root = BuildPageRoot(out UiTemplateListBox listBox, out _);
        var member = new RuntimeFellowMemberSnapshot(0x50000001u, "Alice", 12, 100, 80, 60, 100, 80, 60, false);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, Revision = 1, MemberCount = 1, LeaderGuid = 0xFFu },
            Members = [member],
            LocalPlayerGuid = 0xFFu, // leader, so Dismiss/Leader are enabled once a fellow is selected
        };
        SocialFellowshipPageController.Bind(root, b.Build());

        UiElement row = Assert.Single(listBox.ViewportForTest!.Children);
        UiText name = Assert.IsType<UiText>(UiElement.FindDescendant(row, RowNameTextId));
        name.OnClick!(); // selects Alice, per SelectFellow

        ((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).OnClick!();
        ((UiButton)UiElement.FindDescendant(root, LeaderButtonId)!).OnClick!();

        Assert.Contains("dismiss:50000001", b.Calls);
        Assert.Contains("assign-leader:50000001", b.Calls);
        Assert.Equal(0x50000001u, b.Selection.SelectedObjectId);
    }

    // ── Button enable rules (lane B §2.8) ───────────────────────────────

    [Fact]
    public void ButtonStates_NonLeader_DisbandOpenLeaderDismiss_AreDisabled_QuitStaysEnabled()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, LeaderGuid = 0x1u },
            LocalPlayerGuid = 0x2u, // not the leader
        };
        SocialFellowshipPageController.Bind(root, b.Build());

        Assert.True(((UiButton)UiElement.FindDescendant(root, QuitButtonId)!).Enabled);
        Assert.False(((UiButton)UiElement.FindDescendant(root, DisbandButtonId)!).Enabled);
        Assert.False(((UiButton)UiElement.FindDescendant(root, OpenButtonId)!).Enabled);
        Assert.False(((UiButton)UiElement.FindDescendant(root, LeaderButtonId)!).Enabled);
        Assert.False(((UiButton)UiElement.FindDescendant(root, DismissButtonId)!).Enabled);
    }

    [Fact]
    public void ButtonStates_Leader_DisbandAndOpen_AreEnabled()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, LeaderGuid = 0x1u },
            LocalPlayerGuid = 0x1u,
        };
        SocialFellowshipPageController.Bind(root, b.Build());

        Assert.True(((UiButton)UiElement.FindDescendant(root, DisbandButtonId)!).Enabled);
        Assert.True(((UiButton)UiElement.FindDescendant(root, OpenButtonId)!).Enabled);
    }

    [Fact]
    public void RecruitButton_DisabledWhenFellowshipIsFull()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var selection = new SelectionState();
        selection.Select(0x60000001u, SelectionChangeSource.World);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true, MemberCount = 9, LeaderGuid = 1u },
            LocalPlayerGuid = 1u,
            Selection = selection,
        };
        SocialFellowshipPageController.Bind(root, b.Build());

        Assert.False(((UiButton)UiElement.FindDescendant(root, RecruitButtonId)!).Enabled);
    }


    [Fact]
    public void Checkbox_Click_TogglesAndWritesTheCharacterOption()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        var shareXp = (UiButton)UiElement.FindDescendant(root, ShareXpCheckboxId)!;
        Assert.True(shareXp.SuppressSelfToggle);
        Assert.False(shareXp.Selected);

        shareXp.OnClick!();

        Assert.True(b.Options[CharacterOptionId.FellowshipShareXP]);
        controller.Tick();                 // the seeding mirrors the store
        Assert.True(shareXp.Selected);

        shareXp.OnClick!();                // and the toggle works BOTH ways
        Assert.False(b.Options[CharacterOptionId.FellowshipShareXP]);
        controller.Tick();
        Assert.False(shareXp.Selected);
    }

    [Fact]
    public void Checkbox_RefreshesFromLiveState_WhenTheOtherSurfaceWrites()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = false } };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        var ignore = (UiButton)UiElement.FindDescendant(root, IgnoreCheckboxId)!;
        Assert.False(ignore.Selected);

        b.Options[CharacterOptionId.IgnoreFellowshipRequests] = true;
        controller.Tick();

        Assert.True(ignore.Selected);
    }

    // ── D4: 0x00A6 panel-open declaration ───────────────────────────────

    [Fact]
    public void SetPageVisible_SendsPanelOpen_OnlyOnATransition()
    {
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder { Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true } };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        b.Calls.Clear(); // discard any Bind-time noise

        controller.SetPageVisible(true);
        Assert.Contains("set-panel-open:True", b.Calls);

        b.Calls.Clear();
        controller.SetPageVisible(true); // no transition -> no second send
        Assert.Empty(b.Calls);

        controller.SetPageVisible(false);
        Assert.Contains("set-panel-open:False", b.Calls);
    }

    [Fact]
    public void SetPageVisible_DoesNotLatch_WhenDeclarationDropped_SoItRetriesInWorld()
    {
        // MUST-FIX 3 re-fix (FA4 re-review REOPEN): a declaration attempted
        // before world entry is dropped (Inactive). The latch must NOT advance
        // on a dropped publish — otherwise the in-world retry is deduplicated
        // away and the server never learns the panel is open (fellow vitals
        // freeze). This is the widget-level root of the reconnect bug.
        UiElement root = BuildPageRoot(out _, out _);
        var b = new FellowshipBindingsBuilder
        {
            Snapshot = new RuntimeFellowshipSnapshot { IsInFellowship = true },
            PanelOpenInWorld = false, // pre-world: SetPanelOpen returns Inactive
        };
        SocialFellowshipPageController controller = SocialFellowshipPageController.Bind(root, b.Build())!;
        b.Calls.Clear();

        controller.SetPageVisible(true);
        Assert.Contains("set-panel-open:True", b.Calls); // attempted, dropped
        b.Calls.Clear();

        // In world now: the SAME visible=true re-attempts (not deduplicated,
        // because the dropped attempt never latched) and this time it sticks.
        b.PanelOpenInWorld = true;
        controller.SetPageVisible(true);
        Assert.Contains("set-panel-open:True", b.Calls);
    }
}
