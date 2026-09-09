using System.IO;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Runtime;
using DatReaderWriter;
using DatReaderWriter.Options;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "Manual")]
[Trait("ManualTask", "LiveMountProbe")]
public sealed class SocialPanelLiveMountProbeTests
{
    [Fact]
    public void ProbeLiveMountShapes()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? root = LayoutImporter.ImportInfos(
            dats, SocialPanelController.HostLayoutId, SocialPanelController.SlotElementId);
        Assert.NotNull(root);
        ImportedLayout layout = LayoutImporter.Build(
            root!, _ => (1u, 8, 8), null, null, strings.Resolve);

        Console.WriteLine(
            $"[socialprobe] root id=0x{root!.Id:X8} type={root.Type} "
            + $"({root.X},{root.Y} {root.Width}x{root.Height}) children={root.Children.Count} "
            + $"restorePrevious={(root.TryGetEffectiveBool(RetailPanelUiController.RestorePreviousPropertyId, out bool rp) && rp)}");
        foreach (ElementInfo c in root.Children)
        {
            string p12 = c.TryGetEffectiveProperty(0x12u, out var v12) ? $"0x{v12.UnsignedValue:X8}" : "ABSENT";
            string p57 = c.TryGetEffectiveProperty(0x57u, out var v57) ? $"{v57.Kind}=0x{v57.UnsignedValue:X8}" : "ABSENT";
            Console.WriteLine(
                $"[socialprobe]   root-child 0x{c.Id:X8} type=0x{c.Type:X8} ({c.X},{c.Y} {c.Width}x{c.Height}) "
                + $"kids={c.Children.Count} P0x12={p12} P0x57={p57}");
        }
        {
            string rootP57 = root.TryGetEffectiveProperty(0x57u, out var rv57) ? $"{rv57.Kind}=0x{rv57.UnsignedValue:X8}" : "ABSENT";
            Console.WriteLine($"[socialprobe] root P0x57={rootP57}");
        }

        UiTabPanel tabs = Assert.IsType<UiTabPanel>(layout.Root);
        Console.WriteLine($"[socialprobe] tab table entries={tabs.Tabs.Count}");
        foreach (UiTabTableEntry t in tabs.Tabs)
            Console.WriteLine(
                $"[socialprobe]   button=0x{t.ButtonElementId:X8} page=0x{t.PageElementId:X8} default={t.IsDefault}");
        Assert.True(tabs.Tabs.Count >= 4);

        // Production mount: activate behavior exactly like SocialPanelController.ActivateTabs.
        tabs.ActivateTabBehavior();
        Assert.Empty(tabs.UnresolvedEntries);

        bool sawVisiblePage = false;
        string? visiblePageName = null;
        foreach ((uint pageId, string name) in new[]
        {
            (0x10000513u, "Friends"),
            (0x10000291u, "Allegiance"),
            (0x10000292u, "Fellowship"),
            (0x1000054Au, "Squelch"),
        })
        {
            UiElement? page = UiElement.FindDescendant(tabs, pageId);
            Console.WriteLine(
                $"[socialprobe] page {name} 0x{pageId:X8} -> {(page is null ? "MISSING" : page.GetType().Name)} "
                + $"Visible={page?.Visible}");
            Assert.NotNull(page);
            if (page!.Visible)
            {
                Assert.False(
                    sawVisiblePage,
                    $"page exclusivity violated: both '{visiblePageName}' and '{name}' report Visible=true after ActivateTabBehavior()");
                sawVisiblePage = true;
                visiblePageName = name;
            }
        }
        Assert.True(sawVisiblePage, "no page reports Visible=true after ActivateTabBehavior()");
        Assert.Equal("Allegiance", visiblePageName);

        // Fellowship empty/full frame pair.
        foreach ((uint id, string name) in new[]
        {
            (0x1000026Bu, "NotInAFellowshipFrame"),
            (0x10000275u, "InAFellowshipFrame"),
        })
        {
            UiElement? el = UiElement.FindDescendant(tabs, id);
            Console.WriteLine($"[socialprobe] fellowship frame {name} 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
            Assert.NotNull(el);
        }

        UiElement? nameField = UiElement.FindDescendant(tabs, 0x1000026Fu);
        Console.WriteLine(
            $"[socialprobe] fellowship name-entry field 0x1000026F -> {(nameField is null ? "MISSING" : nameField.GetType().Name)}");
        Assert.IsType<UiField>(nameField);

        foreach ((uint id, string name) in new[]
        {
            (0x10000274u, "CreateFellowshipButton"),
            (0x1000027Bu, "FellowLeaderButton"),
            (0x1000027Cu, "FellowQuitButton"),
            (0x1000027Du, "FellowOpenButton"),
            (0x1000027Eu, "FellowRecruitButton"),
            (0x1000027Fu, "FellowDismissButton"),
            (0x10000280u, "FellowDisbandButton"),
            (0x10000270u, "IgnoreFellowshipRequestsCheckbox"),
            (0x10000271u, "FellowshipAutoAcceptRequestsCheckbox"),
            (0x10000272u, "FellowshipShareXPCheckbox"),
            (0x10000273u, "FellowshipShareLootCheckbox"),
        })
        {
            UiElement? el = UiElement.FindDescendant(tabs, id);
            Console.WriteLine($"[socialprobe] fellowship control {name} 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
            Assert.IsType<UiButton>(el);
        }

        UiElement? fellowshipListBoxEl = UiElement.FindDescendant(tabs, 0x10000279u);
        UiTemplateListBox fellowshipListBox = Assert.IsType<UiTemplateListBox>(fellowshipListBoxEl);
        Console.WriteLine(
            $"[socialprobe] fellowship ListBox 0x10000279 templates={fellowshipListBox.Templates.Count} "
            + $"scrollbar=0x{fellowshipListBox.ScrollbarElementId:X8}");
        Assert.NotEmpty(fellowshipListBox.Templates);
        UiTemplateListEntry fellowRowTemplate = fellowshipListBox.Templates[0];

        var rowTemplates = new RowTemplateResolver(
            (layoutId, elementId) => LayoutImporter.ImportInfos(dats, layoutId, elementId),
            info => LayoutImporter.Build(info, _ => (1u, 8, 8), null, null, strings.Resolve).Root);
        UiElement? fellowRow = rowTemplates.Resolve(
            fellowRowTemplate.TemplateLayoutId, fellowRowTemplate.TemplateElementId);
        Console.WriteLine(
            $"[socialprobe] fellowship row template 0x{fellowRowTemplate.TemplateLayoutId:X8}/"
            + $"0x{fellowRowTemplate.TemplateElementId:X8} -> {(fellowRow is null ? "IMPORT NULL" : fellowRow.GetType().Name)}");
        Assert.NotNull(fellowRow);

        foreach ((uint id, string name, Type expectedType) in new (uint, string, Type)[]
        {
            (0x10000283u, "FellowName", typeof(UiText)),
            (0x10000284u, "FellowStats", typeof(UiText)),
            (0x10000285u, "HealthMeter", typeof(UiMeter)),
            (0x10000287u, "StaminaMeter", typeof(UiMeter)),
            (0x10000289u, "ManaMeter", typeof(UiMeter)),
        })
        {
            UiElement? el = UiElement.FindDescendant(fellowRow!, id);
            Console.WriteLine($"[socialprobe] fellowship row field {name} 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
            Assert.IsType(expectedType, el);
        }

        foreach (string retailName in new[]
        {
            "IgnoreFellowshipRequests", "FellowshipAutoAcceptRequests",
            "FellowshipShareXP", "FellowshipShareLoot",
        })
        {
            string? label = strings.Resolve(0x23000003u, DatStringResolver.ComputeHash($"ID_PlayerOption_{retailName}"));
            Console.WriteLine($"[socialprobe] checkbox label ID_PlayerOption_{retailName} -> '{label}'");
            Assert.False(
                string.IsNullOrEmpty(label),
                $"checkbox label ID_PlayerOption_{retailName} (table 0x23000003) did not resolve.");
        }

        string? openLabel = strings.Resolve(
            0x23000001u, DatStringResolver.ComputeHash("ID_Fellowship_OpenFellowshipButtonText"));
        string? closeLabel = strings.Resolve(
            0x23000001u, DatStringResolver.ComputeHash("ID_Fellowship_CloseFellowshipButtonText"));
        Console.WriteLine($"[socialprobe] fellowship caption ID_Fellowship_OpenFellowshipButtonText -> '{openLabel}'");
        Console.WriteLine($"[socialprobe] fellowship caption ID_Fellowship_CloseFellowshipButtonText -> '{closeLabel}'");
        Assert.Equal("Open", openLabel);
        Assert.Equal("Close", closeLabel);

        UiElement? fellowshipPageForBind = UiElement.FindDescendant(tabs, 0x10000292u);
        Assert.NotNull(fellowshipPageForBind);
        var originalOut = Console.Out;
        var capture = new StringWriter();
        Console.SetOut(capture);
        SocialFellowshipPageController? fellowshipController;
        try
        {
            fellowshipController = SocialFellowshipPageController.Bind(
                fellowshipPageForBind!,
                new SocialFellowshipPageController.Bindings(
                    Snapshot: () => new RuntimeFellowshipSnapshot(),
                    Members: () => [],
                    TemplateResolver: rowTemplates.Resolve,
                    Create: (_, _) => default,
                    Recruit: _ => default,
                    Dismiss: _ => default,
                    Quit: _ => default,
                    AssignLeader: _ => default,
                    SetOpen: _ => default,
                    SetPanelOpen: _ => default,
                    Selection: new AcDream.Core.Selection.SelectionState(),
                    LocalPlayerGuid: () => 0u,
                    CurrentCharacterOption: _ => false,
                    SetCharacterOption: (_, _) => { },
                    ResolveString: (tableId, stringId) => strings.Resolve(tableId, stringId)));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        string bindLog = capture.ToString();
        Console.WriteLine($"[socialprobe] fellowship Bind() console output:\n{bindLog}");
        Assert.NotNull(fellowshipController);
        Assert.DoesNotContain("not found", bindLog);

        // Allegiance signature elements.
        foreach ((uint id, string name) in new[]
        {
            (0x10000255u, "MonarchField"),
            (0x1000025Au, "PatronField"),
            (0x10000260u, "VassalListBox"),
            (0x10000263u, "SwearButton"),
            (0x10000264u, "BreakButton"),
            (0x10000265u, "KickButton"),
        })
        {
            UiElement? el = UiElement.FindDescendant(tabs, id);
            Console.WriteLine($"[socialprobe] allegiance element {name} 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
            Assert.NotNull(el);
        }

        UiElement? allegiancePage = UiElement.FindDescendant(tabs, 0x10000291u);
        Assert.NotNull(allegiancePage);
        int passupCount = CountDescendants(allegiancePage!, 0x10000492u);
        Console.WriteLine($"[socialprobe] 0x10000492 occurrences under allegiance page = {passupCount}");
        Assert.Equal(2, passupCount);

        UiElement? monarchField = UiElement.FindDescendant(tabs, 0x10000255u);
        UiElement? patronField = UiElement.FindDescendant(tabs, 0x1000025Au);
        Assert.NotNull(monarchField);
        Assert.NotNull(patronField);
        UiElement? monarchIsPatronSubBlock = UiElement.FindDescendant(monarchField!, 0x10000490u);
        Console.WriteLine(
            $"[socialprobe] allegiance sub-block 0x10000490 (under monarch field) -> "
            + $"{(monarchIsPatronSubBlock is null ? "MISSING" : monarchIsPatronSubBlock.GetType().Name)}");
        Assert.NotNull(monarchIsPatronSubBlock);
        UiElement? monarchScopedPassup = UiElement.FindDescendant(monarchIsPatronSubBlock!, 0x10000492u);
        UiElement? patronScopedPassup = UiElement.FindDescendant(patronField!, 0x10000492u);
        Console.WriteLine(
            $"[socialprobe] 0x10000492 under 0x10000490 -> {(monarchScopedPassup is null ? "MISSING" : "0x" + monarchScopedPassup.DatElementId.ToString("X8"))}; "
            + $"under patron field -> {(patronScopedPassup is null ? "MISSING" : "0x" + patronScopedPassup.DatElementId.ToString("X8"))}");
        Assert.NotNull(monarchScopedPassup);
        Assert.NotNull(patronScopedPassup);
        Assert.NotSame(monarchScopedPassup, patronScopedPassup);

        UiElement? vassalListBoxEl = UiElement.FindDescendant(tabs, 0x10000260u);
        UiTemplateListBox vassalListBox = Assert.IsType<UiTemplateListBox>(vassalListBoxEl);
        Console.WriteLine(
            $"[socialprobe] allegiance ListBox 0x10000260 templates={vassalListBox.Templates.Count} "
            + $"scrollbar=0x{vassalListBox.ScrollbarElementId:X8}");
        Assert.NotEmpty(vassalListBox.Templates);
        UiTemplateListEntry vassalRowTemplate = vassalListBox.Templates[0];

        UiElement? vassalRow = rowTemplates.Resolve(
            vassalRowTemplate.TemplateLayoutId, vassalRowTemplate.TemplateElementId);
        Console.WriteLine(
            $"[socialprobe] allegiance row template 0x{vassalRowTemplate.TemplateLayoutId:X8}/"
            + $"0x{vassalRowTemplate.TemplateElementId:X8} -> {(vassalRow is null ? "IMPORT NULL" : vassalRow.GetType().Name)}");
        Assert.NotNull(vassalRow);

        foreach ((uint id, string name, Type expectedType) in new (uint, string, Type)[]
        {
            (0x10000268u, "VassalName", typeof(UiText)),
            (0x10000269u, "VassalExperiencePassedUp", typeof(UiText)),
        })
        {
            UiElement? el = UiElement.FindDescendant(vassalRow!, id);
            Console.WriteLine($"[socialprobe] allegiance row field {name} 0x{id:X8} -> {(el is null ? "MISSING" : el.GetType().Name)}");
            Assert.IsType(expectedType, el);
        }
        UiElement? offlineMarker = UiElement.FindDescendant(vassalRow!, 0x100004AAu);
        Console.WriteLine($"[socialprobe] allegiance row field OfflineMarker 0x100004AA -> {(offlineMarker is null ? "MISSING" : offlineMarker.GetType().Name)}");
        Assert.NotNull(offlineMarker);

        UiElement? ignoreRequestsCheckbox = UiElement.FindDescendant(tabs, 0x10000262u);
        Console.WriteLine(
            $"[socialprobe] allegiance checkbox IgnoreAllegianceRequests 0x10000262 -> "
            + $"{(ignoreRequestsCheckbox is null ? "MISSING" : ignoreRequestsCheckbox.GetType().Name)}");
        Assert.IsType<UiButton>(ignoreRequestsCheckbox);

        string? ignoreRequestsLabel = strings.Resolve(
            0x23000003u, DatStringResolver.ComputeHash("ID_PlayerOption_IgnoreAllegianceRequests"));
        Console.WriteLine($"[socialprobe] checkbox label ID_PlayerOption_IgnoreAllegianceRequests -> '{ignoreRequestsLabel}'");
        Assert.False(string.IsNullOrEmpty(ignoreRequestsLabel));

        foreach (string key in new[]
        {
            "ID_Allegiance_MonarchLabel",
            "ID_Allegiance_PatronSlashMonarchLabel",
            "ID_Allegiance_SwearConfirmation",
            "ID_Allegiance_BreakConfirmation",
            "ID_Allegiance_KickConfirmation",
        })
        {
            string? value = strings.Resolve(0x23000001u, DatStringResolver.ComputeHash(key));
            Console.WriteLine($"[socialprobe] allegiance string {key} -> '{value}'");
            Assert.False(string.IsNullOrEmpty(value));
        }

        UiElement? allegiancePageForBind = UiElement.FindDescendant(tabs, 0x10000291u);
        Assert.NotNull(allegiancePageForBind);
        var allegianceOriginalOut = Console.Out;
        var allegianceCapture = new StringWriter();
        Console.SetOut(allegianceCapture);
        SocialAllegiancePageController? allegianceController;
        try
        {
            allegianceController = SocialAllegiancePageController.Bind(
                allegiancePageForBind!,
                new SocialAllegiancePageController.Bindings(
                    Snapshot: () => new RuntimeAllegianceSnapshot(),
                    Monarch: () => null,
                    Patron: _ => null,
                    Member: _ => null,
                    Vassals: _ => [],
                    Swear: _ => default,
                    Break: _ => default,
                    Kick: _ => default,
                    SetUpdateSubscription: _ => default,
                    Selection: new AcDream.Core.Selection.SelectionState(),
                    LocalPlayerGuid: () => 0u,
                    CurrentCharacterOption: _ => false,
                    SetCharacterOption: (_, _) => { },
                    TemplateResolver: rowTemplates.Resolve,
                    ResolveString: (tableId, stringId) => strings.Resolve(tableId, stringId),
                    ResolveWorldObjectName: _ => null,
                    ShowConfirmation: (_, _) => 0u));
        }
        finally
        {
            Console.SetOut(allegianceOriginalOut);
        }
        string allegianceBindLog = allegianceCapture.ToString();
        Console.WriteLine($"[socialprobe] allegiance Bind() console output:\n{allegianceBindLog}");
        Assert.NotNull(allegianceController);
        Assert.DoesNotContain("not found", allegianceBindLog);

        foreach (UiTabTableEntry t in tabs.Tabs)
        {
            UiElement? button = UiElement.FindDescendant(tabs, t.ButtonElementId);
            string? caption = button switch
            {
                UiText text => text.LinesProvider?.Invoke() is { Count: > 0 } lines ? lines[0].Text : null,
                UiButton btn => btn.Label,
                _ => null,
            };
            Console.WriteLine(
                $"[socialprobe] tab button 0x{t.ButtonElementId:X8} ({button?.GetType().Name}) caption='{caption}'");
            Assert.False(string.IsNullOrEmpty(caption));
        }

        ElementInfo? notInFellowshipFrame = FindInfo(root, 0x1000026Bu);
        ElementInfo? inFellowshipFrame = FindInfo(root, 0x10000275u);
        Console.WriteLine(
            $"[socialprobe] 0x1000026B (NotInAFellowshipFrame) children: "
            + string.Join(",", (notInFellowshipFrame?.Children ?? new()).ConvertAll(c => $"0x{c.Id:X8}")));
        Console.WriteLine(
            $"[socialprobe] 0x10000275 (InAFellowshipFrame) children: "
            + string.Join(",", (inFellowshipFrame?.Children ?? new()).ConvertAll(c => $"0x{c.Id:X8}")));

        foreach (uint pageId in new[] { 0x10000513u, 0x10000291u, 0x10000292u, 0x1000054Au })
        {
            ElementInfo? pageInfo = FindInfo(root, pageId);
            if (pageInfo is null)
            {
                Console.WriteLine($"[socialprobe] page-info 0x{pageId:X8} MISSING from ElementInfo tree");
                continue;
            }
            Console.WriteLine(
                $"[socialprobe] page-info 0x{pageId:X8} P0x57={(pageInfo.TryGetEffectiveProperty(0x57u, out var p57) ? p57.UnsignedValue.ToString() : "ABSENT")} children={pageInfo.Children.Count}");
            DumpInfoTree(pageInfo, 1, maxDepth: 3);
        }

        foreach ((uint tLayout, uint tElement, string name) in new[]
        {
            (0x2100005Du, 0x10000519u, "FriendsRow"),
            (0x21000060u, 0x10000541u, "SquelchRow"),
        })
        {
            ElementInfo? row = LayoutImporter.ImportInfos(dats, tLayout, tElement);
            if (row is null)
            {
                Console.WriteLine($"[socialprobe] {name} template 0x{tLayout:X8}/0x{tElement:X8} IMPORT NULL");
                continue;
            }
            Console.WriteLine($"[socialprobe] {name} template:");
            DumpInfoTree(row, 1, maxDepth: 3);
        }
    }

    private static void DumpInfoTree(ElementInfo info, int depth, int maxDepth)
    {
        if (depth > maxDepth) return;
        string templates = info.TemplateList.Count > 0
            ? $" templates={info.TemplateList.Count}[{string.Join(",", info.TemplateList.ConvertAll(t => $"0x{t.TemplateLayoutId:X8}/0x{t.TemplateElementId:X8}"))}] scrollbar=0x{info.ScrollbarElementId:X8}"
            : "";
        Console.WriteLine(
            $"[socialprobe] {new string(' ', depth * 2)}0x{info.Id:X8} type=0x{info.Type:X8} "
            + $"({info.X},{info.Y} {info.Width}x{info.Height}) kids={info.Children.Count}{templates}");
        foreach (ElementInfo c in info.Children)
            DumpInfoTree(c, depth + 1, maxDepth);
    }

    [Fact]
    public void ProbeSocialClickRouting()
    {
        if (Environment.GetEnvironmentVariable("ACDREAM_PROBE_LIVE_MOUNT") != "1")
            Assert.Fail("Lane=Manual live-mount probe requires ACDREAM_PROBE_LIVE_MOUNT=1.");

        var datDir = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDir, DatAccessType.Read);
        var strings = new DatStringResolver(dats);

        ElementInfo? rootInfo = LayoutImporter.ImportInfos(
            dats, SocialPanelController.HostLayoutId, SocialPanelController.SlotElementId);
        Assert.NotNull(rootInfo);
        ImportedLayout layout = LayoutImporter.Build(
            rootInfo!, _ => (1u, 8, 8), null, null, strings.Resolve);
        UiTabPanel tabs = Assert.IsType<UiTabPanel>(layout.Root);
        tabs.ActivateTabBehavior();

        var rowTemplates = new RowTemplateResolver(
            (layoutId, elementId) => LayoutImporter.ImportInfos(dats, layoutId, elementId),
            info => LayoutImporter.Build(info, _ => (1u, 8, 8), null, null, strings.Resolve).Root);

        RuntimeFellowshipSnapshot snapshot = default(RuntimeFellowshipSnapshot) with
        {
            Revision = 1,
            IsInFellowship = true,
            Name = "Probe",
            LeaderGuid = 0x50000001u,
        };
        var members = new List<RuntimeFellowMemberSnapshot>
        {
            new(0x50000001u, "Leader", 10, 100, 100, 100, 100, 100, 100, false),
            new(0x50000002u, "Fellow", 12, 120, 120, 120, 120, 120, 120, false),
        };
        var optionWrites = new List<(uint Id, bool Value)>();

        UiElement? fellowshipPage = UiElement.FindDescendant(tabs, 0x10000292u);
        Assert.NotNull(fellowshipPage);
        SocialFellowshipPageController? controller = SocialFellowshipPageController.Bind(
            fellowshipPage!,
            new SocialFellowshipPageController.Bindings(
                Snapshot: () => snapshot,
                Members: () => members,
                TemplateResolver: rowTemplates.Resolve,
                Create: (_, _) => default,
                Recruit: _ => default,
                Dismiss: _ => default,
                Quit: _ => default,
                AssignLeader: _ => default,
                SetOpen: _ => default,
                SetPanelOpen: _ => default,
                Selection: new AcDream.Core.Selection.SelectionState(),
                LocalPlayerGuid: () => 0x50000001u,
                CurrentCharacterOption: _ => false,
                SetCharacterOption: (id, value) => optionWrites.Add(((uint)id, value)),
                ResolveString: (tableId, stringId) => strings.Resolve(tableId, stringId)));
        Assert.NotNull(controller);

        var uiRoot = new UiRoot { Width = 1280, Height = 720 };
        RetailWindowHandle handle = RetailWindowFrame.Mount(
            uiRoot, tabs, _ => (1u, 8, 8),
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.SocialPanel,
                Chrome = RetailWindowChrome.NineSlice,
                Left = 200f,
                Top = 140f,
                ResizeX = false,
                ResizeY = true,
                ResizableEdges = ResizeEdges.Bottom,
                ConstrainDragToParent = true,
                ConstrainResizeToParent = true,
                ContentAnchors = AnchorEdges.Left | AnchorEdges.Top
                    | AnchorEdges.Right | AnchorEdges.Bottom,
                ContentClickThrough = false,
            });
        handle.Show();

        tabs.SwitchTo(0x10000292u);   // the Fellowship page
        controller!.Tick();           // builds the two roster rows

        foreach ((uint id, string name) in new (uint, string)[]
        {
            (0x10000270u, "IgnoreRequestsCheckbox"),
            (0x10000271u, "AutoAcceptCheckbox"),
            (0x10000272u, "ShareXpCheckbox"),
            (0x10000273u, "ShareLootCheckbox"),
            (0x1000027Fu, "DismissButton"),
            (0x10000283u, "RowNameText(first)"),
        })
        {
            UiElement? el = UiElement.FindDescendant(uiRoot, id);
            if (el is null)
            {
                Console.WriteLine($"[clickprobe] {name} 0x{id:X8}: MISSING under the mounted root");
                continue;
            }
            (float ax, float ay) = Absolute(el);
            float cx = ax + el.Width / 2f, cy = ay + el.Height / 2f;
            UiElement? winner = uiRoot.HitTest(cx, cy);
            Console.WriteLine(
                $"[clickprobe] {name} 0x{id:X8}: abs=({ax},{ay} {el.Width}x{el.Height}) "
                + $"hit@({cx},{cy}) -> {(winner is null ? "NULL" : $"{winner.GetType().Name} 0x{winner.DatElementId:X8}")} "
                + $"{(ReferenceEquals(winner, el) ? "SELF" : "NOT-SELF")}");
            for (UiElement? a = el; a is not null; a = a.Parent)
                Console.WriteLine(
                    $"[clickprobe]     ancestor {a.GetType().Name} 0x{a.DatElementId:X8} "
                    + $"({a.Left},{a.Top} {a.Width}x{a.Height}) "
                    + $"Visible={a.Visible} Enabled={a.Enabled} ClickThrough={a.ClickThrough}");
        }

        if (UiElement.FindDescendant(uiRoot, 0x10000270u) is { } cb)
        {
            (float ax, float ay) = Absolute(cb);
            int px = (int)(ax + cb.Width / 2f), py = (int)(ay + cb.Height / 2f);
            uiRoot.OnMouseDown(UiMouseButton.Left, px, py);
            uiRoot.OnMouseUp(UiMouseButton.Left, px, py);
            Console.WriteLine(
                $"[clickprobe] synthetic click on IgnoreRequestsCheckbox -> optionWrites=[{string.Join(",", optionWrites)}]");
        }

        snapshot = snapshot with { IsInFellowship = false, Revision = 2 };
        controller.Tick();
        if (UiElement.FindDescendant(uiRoot, 0x1000026Bu) is { } emptyFrame)
            DumpTexts(emptyFrame, 0);

        optionWrites.Clear();
        if (UiElement.FindDescendant(uiRoot, 0x10000270u) is { } cb2)
        {
            (float ax, float ay) = Absolute(cb2);
            int px = (int)(ax + cb2.Width / 2f), py = (int)(ay + cb2.Height / 2f);
            UiElement? winner = uiRoot.HitTest(px, py);
            Console.WriteLine(
                $"[clickprobe] NOT-in-fellowship hit@({px},{py}) -> "
                + $"{(winner is null ? "NULL" : $"{winner.GetType().Name} 0x{winner.DatElementId:X8}")}");
            uiRoot.OnMouseDown(UiMouseButton.Left, px, py);
            uiRoot.OnMouseUp(UiMouseButton.Left, px, py);
            Console.WriteLine(
                $"[clickprobe] NOT-in-fellowship synthetic click -> optionWrites=[{string.Join(",", optionWrites)}]");
        }

        if (UiElement.FindDescendant(uiRoot, 0x10000279u) is UiTemplateListBox rosterBox
            && rosterBox.Templates.Count > 0)
        {
            ElementInfo? rowInfo = LayoutImporter.ImportInfos(
                dats,
                rosterBox.Templates[0].TemplateLayoutId,
                rosterBox.Templates[0].TemplateElementId);
            if (rowInfo is not null)
                DumpStates(rowInfo, 0);
        }

        // Friends + Squelch action widgets: authored labels → button roles
        // (never guess an id's role).
        foreach (uint pageId in new[] { 0x10000513u, 0x1000054Au })
        {
            if (UiElement.FindDescendant(uiRoot, pageId) is not { } page) continue;
            DumpActionWidgets(page, 0);
        }

        tabs.SwitchTo(0x10000291u);
        foreach (uint id in new[] { 0x10000266u, 0x10000267u, 0x10000268u, 0x10000269u, 0x1000026Au })
        {
            UiElement? el = UiElement.FindDescendant(uiRoot, id);
            if (el is null) continue;
            (float ax, float ay) = Absolute(el);
            UiElement? winner = uiRoot.HitTest(ax + el.Width / 2f, ay + el.Height / 2f);
            Console.WriteLine(
                $"[clickprobe] allegiance 0x{id:X8} {el.GetType().Name} abs=({ax},{ay} {el.Width}x{el.Height}) "
                + $"Visible={el.Visible} Enabled={el.Enabled} ClickThrough={el.ClickThrough} "
                + $"hit -> {(winner is null ? "NULL" : $"{winner.GetType().Name} 0x{winner.DatElementId:X8}")}");
        }
    }

    private static (float X, float Y) Absolute(UiElement el)
    {
        float x = 0, y = 0;
        for (UiElement? a = el; a is not null; a = a.Parent)
        {
            x += a.Left;
            y += a.Top;
        }
        return (x, y);
    }

    private static void DumpStates(ElementInfo info, int depth)
    {
        string states = string.Join(
            ",",
            info.StateMedia.Select(kv => $"'{kv.Key}'=0x{kv.Value.File:X8}"));
        Console.WriteLine(
            $"[clickprobe] {new string(' ', depth * 2)}ROWSTATE 0x{info.Id:X8} type=0x{info.Type:X8} "
            + $"({info.X},{info.Y} {info.Width}x{info.Height}) default='{info.DefaultStateName}' "
            + $"media=[{states}]");
        foreach (ElementInfo c in info.Children)
            DumpStates(c, depth + 1);
    }

    private static void DumpActionWidgets(UiElement el, int depth)
    {
        string extra = el switch
        {
            UiButton b => $" label='{b.Label}'",
            UiField => " FIELD",
            _ => "",
        };
        if (el is UiButton or UiField || depth == 0)
            Console.WriteLine(
                $"[clickprobe] {new string(' ', depth * 2)}{el.GetType().Name} "
                + $"0x{el.DatElementId:X8} ({el.Left},{el.Top} {el.Width}x{el.Height}){extra}");
        foreach (UiElement c in el.Children)
            DumpActionWidgets(c, depth + 1);
    }

    private static void DumpTexts(UiElement el, int depth)
    {
        if (el is UiText text)
        {
            string content = string.Join(
                " \\n ",
                (text.LinesProvider?.Invoke() ?? []).Select(l => l.Text));
            Console.WriteLine(
                $"[clickprobe] {new string(' ', depth * 2)}TEXT 0x{el.DatElementId:X8} "
                + $"({el.Left},{el.Top} {el.Width}x{el.Height}) '{content}'");
        }
        else
        {
            Console.WriteLine(
                $"[clickprobe] {new string(' ', depth * 2)}{el.GetType().Name} 0x{el.DatElementId:X8} "
                + $"({el.Left},{el.Top} {el.Width}x{el.Height})");
        }
        foreach (UiElement c in el.Children)
            DumpTexts(c, depth + 1);
    }

    private static int CountDescendants(UiElement root, uint id)
    {
        int count = root.DatElementId == id ? 1 : 0;
        foreach (UiElement child in root.Children)
            count += CountDescendants(child, id);
        return count;
    }

    private static ElementInfo? FindInfo(ElementInfo root, uint id)
    {
        if (root.Id == id) return root;
        foreach (ElementInfo c in root.Children)
        {
            ElementInfo? found = FindInfo(c, id);
            if (found is not null) return found;
        }
        return null;
    }
}
