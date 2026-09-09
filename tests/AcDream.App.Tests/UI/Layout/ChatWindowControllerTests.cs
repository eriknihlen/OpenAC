using System.Collections.Generic;
using System.Linq;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;
using AcDream.Runtime.Chat;

namespace AcDream.App.Tests.UI.Layout;

public class ChatWindowControllerTests
{
    // ── Null-resolve helper (no GL needed) ─────────────────────────────────
    private static (uint, int, int) NoTex(uint _) => (0u, 0, 0);

    private sealed class CaptureBus : ICommandBus
    {
        public readonly List<object> Published = new();
        public void Publish<T>(T cmd) where T : notnull => Published.Add(cmd!);
    }


    private static (ElementInfo rootInfo, ImportedLayout layout, ChatVM vm) BuildTestTree(
        ChatLog? log = null)
    {
        var transcriptNode = new ElementInfo
        {
            Id = 0x10000011u, Type = 12,  // Type-12, no media → skipped by factory
            X = 16, Y = 0, Width = 458, Height = 74,
        };
        var trackNode = new ElementInfo
        {
            Id = 0x10000012u, Type = 3,
            X = 474, Y = 6, Width = 16, Height = 68,
        };
        var transcriptPanel = new ElementInfo
        {
            Id = 0x10000010u, Type = 3, X = 0, Y = 9, Width = 490, Height = 74,
        };
        transcriptPanel.Children.Add(transcriptNode);
        transcriptPanel.Children.Add(trackNode);

        var menuNode = new ElementInfo
        {
            Id = 0x10000014u, Type = 6, X = 0, Y = 0, Width = 46, Height = 17,
        };
        var inputNode = new ElementInfo
        {
            Id = 0x10000016u, Type = 12,
            X = 46, Y = 0, Width = 398, Height = 17,
        };
        var inputState = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        inputState.Properties.Values[0x16u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = true,
        };
        inputState.Properties.Values[0x20u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = true,
        };
        inputState.Properties.Values[0x27u] = new UiPropertyValue
        {
            Kind = UiPropertyKind.Bool,
            BoolValue = true,
        };
        inputNode.States[UiStateInfo.DirectStateId] = inputState;
        var sendNode = new ElementInfo
        {
            Id = 0x10000019u, Type = 3, X = 444, Y = 0, Width = 46, Height = 17,
        };
        var inputBar = new ElementInfo
        {
            Id = 0x10000013u, Type = 3, X = 0, Y = 83, Width = 490, Height = 17,
        };
        inputBar.Children.Add(menuNode);
        inputBar.Children.Add(inputNode);
        inputBar.Children.Add(sendNode);

        var maxMinNode = new ElementInfo
        {
            Id = 0x1000046Fu, Type = 3, X = 474, Y = 0, Width = 16, Height = 16,
        };

        ElementInfo MakeIndicator(uint id, float y)
        {
            var info = new ElementInfo { Id = id, Type = 1, X = 5, Y = y, Width = 16, Height = 16 };
            info.StateMedia["Normal"] = (0x1u, 1);
            info.StateMedia["Highlight"] = (0x2u, 1);
            return info;
        }
        var unread = new ElementInfo
        {
            Id = 0x1000048Cu, Type = 1, X = 0, Y = 57, Width = 16, Height = 16,
        };
        unread.StateMedia["Normal"] = (0x3u, 1);
        unread.StateMedia["Ghosted"] = (0x4u, 1);

        var indicator1 = MakeIndicator(0x10000522u, 5);
        var indicator2 = MakeIndicator(0x10000523u, 22);
        var indicator3 = MakeIndicator(0x10000524u, 39);
        var indicator4 = MakeIndicator(0x10000525u, 56);

        var root = new ElementInfo
        {
            Id = 0x10000600u, Type = 3, Width = 490, Height = 100,
        };
        root.Children.Add(transcriptPanel);
        root.Children.Add(inputBar);
        root.Children.Add(maxMinNode);
        root.Children.Add(unread);
        root.Children.Add(indicator1);
        root.Children.Add(indicator2);
        root.Children.Add(indicator3);
        root.Children.Add(indicator4);

        var layout = LayoutImporter.Build(root, NoTex, null);
        var vm = new ChatVM(log ?? new ChatLog());
        return (root, layout, vm);
    }

    // ── Test 1: Bind returns non-null with the minimal tree ──────────────────

    [Fact]
    public void Bind_Returns_NonNull_OnValidTree()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
    }

    // ── CT-C1: the unseen-text indicator ────────────────────────────────

    private static ChatWindowController BindController()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        ChatWindowController? ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(ctrl);
        return ctrl!;
    }

    [Fact]
    public void ReachingTheBottomClearsTheUnseenFlagHoweverYouGotThere()
    {
        ChatWindowController ctrl = BindController();
        ctrl.Transcript.Scroll.SetExtents(contentHeight: 500, viewHeight: 100);
        ctrl.Transcript.Scroll.SetScrollY(0);        // scrolled up
        Assert.False(ctrl.Transcript.Scroll.AtEnd);

        ctrl.Transcript.Scroll.ScrollToEnd();
        ctrl.UpdateUnreadIndicator();

        Assert.True(ctrl.Transcript.Scroll.AtEnd);
    }

    [Fact]
    public void TheIndicatorFollowsItsAuthoredPerStateVisibility()
    {
        ChatWindowController ctrl = BindController();
        UiElement indicator = Assert.IsAssignableFrom<UiElement>(
            ctrl.UnreadIndicatorForTest);

        Assert.False(indicator.Visible);          // Ghosted at rest

        ctrl.Transcript.Scroll.SetExtents(contentHeight: 500, viewHeight: 100);
        ctrl.Transcript.Scroll.SetScrollY(0);
        ctrl.SetUnreadForTest(true);
        ctrl.UpdateUnreadIndicator();
        Assert.True(indicator.Visible);           // Normal once text is unseen

        ctrl.Transcript.Scroll.ScrollToEnd();
        ctrl.UpdateUnreadIndicator();
        Assert.False(indicator.Visible);          // back to Ghosted
    }

    [Fact]
    public void TheAuthoredHandOffEndsTheFlashEvenWhileStillScrolledUp()
    {
        ChatWindowController ctrl = BindController();
        var indicator = Assert.IsType<UiButton>(ctrl.UnreadIndicatorForTest);

        ctrl.Transcript.Scroll.SetExtents(contentHeight: 500, viewHeight: 100);
        ctrl.Transcript.Scroll.SetScrollY(0);
        ctrl.SetUnreadForTest(true);
        ctrl.UpdateUnreadIndicator();
        Assert.True(indicator.Visible);

        // Stand in for the sequence reaching its terminal State step.
        indicator.TrySetRetailState(UiButtonStateMachine.Ghosted);
        ctrl.UpdateUnreadIndicator();

        Assert.False(indicator.Visible);
        Assert.False(ctrl.Transcript.Scroll.AtEnd);   // still scrolled up
    }

    [Fact]
    public void TheFlashIsStartedOnceRatherThanEveryFrame()
    {
        ChatWindowController ctrl = BindController();
        var indicator = Assert.IsType<UiButton>(ctrl.UnreadIndicatorForTest);

        ctrl.Transcript.Scroll.SetExtents(contentHeight: 500, viewHeight: 100);
        ctrl.Transcript.Scroll.SetScrollY(0);
        ctrl.SetUnreadForTest(true);
        ctrl.UpdateUnreadIndicator();

        indicator.TrySetRetailState(UiButtonStateMachine.Ghosted);
        for (int frame = 0; frame < 5; frame++)
            ctrl.UpdateUnreadIndicator();

        Assert.Equal("Ghosted", indicator.ActiveState);
    }

    [Fact]
    public void ClickingTheIndicatorJumpsToTheNewestText()
    {
        ChatWindowController ctrl = BindController();
        ctrl.Transcript.Scroll.SetExtents(contentHeight: 500, viewHeight: 100);
        ctrl.Transcript.Scroll.SetScrollY(0);
        Assert.False(ctrl.Transcript.Scroll.AtEnd);

        ctrl.ScrollToNewestAndClearUnread();

        Assert.True(ctrl.Transcript.Scroll.AtEnd);
    }

    [Fact]
    public void StartTell_PrefillsTheEntryAndPutsTheCaretAtTheEnd()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        ChatWindowController? ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(ctrl);

        ctrl!.StartTell("Dww");

        Assert.Equal("@tell Dww, ", ctrl.Input.Text);

        Assert.Equal("@tell Dww, ".Length, ctrl.Input.CaretPos);
    }

    // ── Talk-focus specials: "Tell to X" / "Squelch (ignore) X" ─────────────

    [Fact]
    public void TalkFocusSpecials_CarryTheSelectedName_AndActOnIt()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        string? selected = "Dww";

        ChatWindowController? ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex,
            selectedTargetName: () => selected);
        Assert.NotNull(ctrl);
        UiMenu menu = Assert.IsType<UiMenu>(layout.FindElement(0x10000014u));

        menu.OnOpen!.Invoke();
        Assert.Equal("Squelch (ignore) Dww", menu.Items[0].Label);
        Assert.Equal("Tell to Dww", menu.Items[1].Label);

        menu.OnSelect!.Invoke(menu.Items[1].Payload);
        ctrl!.Input.OnSubmit!.Invoke("hello");
        SendChatCmd tell = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Tell, tell.Channel);
        Assert.Equal("Dww", tell.TargetName);
        Assert.Equal("hello", tell.Text);

        // Squelch reaches the already-registered /squelch verb rather than
        // reimplementing the request.
        bus.Published.Clear();
        menu.OnSelect.Invoke(menu.Items[0].Payload);
        ExecuteClientCommandCmd squelch =
            Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.Squelch, squelch.Command);
        Assert.Equal("Dww", squelch.Arguments);
    }

    [Fact]
    public void TalkFocusSpecials_AreInertAndUnnamedWithNothingSelected()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        ChatWindowController? ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex,
            selectedTargetName: () => null);
        Assert.NotNull(ctrl);
        UiMenu menu = Assert.IsType<UiMenu>(layout.FindElement(0x10000014u));

        menu.OnOpen!.Invoke();
        Assert.Equal("Squelch (ignore) Selected", menu.Items[0].Label);
        Assert.Equal("Tell to Selected", menu.Items[1].Label);

        Assert.False(menu.EnabledProvider!(menu.Items[0].Payload));
        Assert.False(menu.EnabledProvider(menu.Items[1].Payload));

        menu.OnSelect!.Invoke(menu.Items[1].Payload);
        menu.OnSelect.Invoke(menu.Items[0].Payload);
        Assert.Empty(bus.Published);
    }


    [Fact]
    public void Bind_Transcript_IsChildOfTranscriptPanel()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        var panel = layout.FindElement(0x10000010u);
        Assert.NotNull(panel);
        Assert.Contains(ctrl!.Transcript, panel!.Children);
    }


    [Fact]
    public void Bind_Transcript_ForcesScrollableTextMode()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        Assert.False(ctrl!.Transcript.Centered);
        Assert.False(ctrl.Transcript.RightAligned);
    }

    [Fact]
    public void Bind_Input_IsChildOfInputBar()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        var bar = layout.FindElement(0x10000013u);
        Assert.NotNull(bar);
        Assert.Contains(ctrl!.Input, bar!.Children);
    }

    [Fact]
    public void TranscriptLayout_IsReusedUntilContentOrWidthChanges()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex)!;

        vm.ShowSystemMessage("one wrapped transcript line");
        IReadOnlyList<UiText.Line> first = ctrl.Transcript.LinesProvider();
        IReadOnlyList<UiText.Line> unchanged = ctrl.Transcript.LinesProvider();

        Assert.Same(first, unchanged);
        Assert.Equal(1, ctrl.TranscriptLayoutBuildCount);

        vm.ShowSystemMessage("second line");
        IReadOnlyList<UiText.Line> appended = ctrl.Transcript.LinesProvider();
        Assert.NotSame(first, appended);
        Assert.Equal(2, ctrl.TranscriptLayoutBuildCount);

        ctrl.Transcript.Width -= 40f;
        IReadOnlyList<UiText.Line> resized = ctrl.Transcript.LinesProvider();
        Assert.NotSame(appended, resized);
        Assert.Equal(3, ctrl.TranscriptLayoutBuildCount);

        vm.Clear();
        Assert.Empty(ctrl.Transcript.LinesProvider());
        Assert.Equal(4, ctrl.TranscriptLayoutBuildCount);
    }

    [Fact]
    public void TranscriptLines_OutOfRangeLogTextType_CarriesPreviousLinesColor()
    {
        var log = new ChatLog();
        var (rootInfo, layout, vm) = BuildTestTree(log);
        var bus = new CaptureBus();
        var filters = new ChatWindowState();
        filters.SetFilter(ChatWindowState.MainWindowId, ulong.MaxValue);
        var ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => bus, filters, null, null, NoTex)!;

        log.OnSystemMessage("first", chatType: 0x05u);
        log.OnSystemMessage("middle", chatType: 0x22u);
        log.OnSystemMessage("third", chatType: 0x00u);

        IReadOnlyList<UiText.Line> lines = ctrl.Transcript.LinesProvider();

        Assert.Equal(3, lines.Count);
        Assert.Equal(lines[0].Color, lines[1].Color);
        Assert.NotEqual(lines[0].Color, lines[2].Color);
    }


    [Fact]
    public void Bind_InputSubmit_PublishesSendChatCmd()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        ctrl!.Input.OnSubmit!.Invoke("hello world");

        Assert.Single(bus.Published);
        var cmd = Assert.IsType<SendChatCmd>(bus.Published[0]);
        Assert.Equal("hello world", cmd.Text);
    }

    [Fact]
    public void Bind_LifestoneSubmit_PublishesTypedClientCommand()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        ctrl!.Input.OnSubmit!.Invoke("/ls");

        var command = Assert.IsType<ExecuteClientCommandCmd>(Assert.Single(bus.Published));
        Assert.Equal(ClientCommandId.LifestoneRecall, command.Command);
    }


    [Fact]
    public void Bind_ChannelChange_UpdatesSubmitChannel()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        ctrl!.Menu.OnSelect!.Invoke((object?)ChatChannelKind.General);
        ctrl.Input.OnSubmit!.Invoke("hey all");

        Assert.Single(bus.Published);
        var cmd = Assert.IsType<SendChatCmd>(bus.Published[0]);
        Assert.Equal(ChatChannelKind.General, cmd.Channel);
    }

    // ── Test 6: Bind returns null when required elements are absent ──────────

    [Fact]
    public void Bind_Returns_Null_WhenTranscriptPanelMissing()
    {
        // Build a layout that is missing the transcript panel entirely.
        var root = new ElementInfo { Id = 0x10000600u, Type = 3, Width = 490, Height = 100 };

        var layout = LayoutImporter.Build(root, NoTex, null);
        var vm = new ChatVM(new ChatLog());
        var bus = new CaptureBus();

        var ctrl = ChatWindowController.Bind(root, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.Null(ctrl);
    }


    [Fact]
    public void Bind_InputField_WithNoImportedLayoutPolicy_NeverOverflowsOnNarrowerResize()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(ctrl);
        Assert.Null(ctrl!.Input.LayoutPolicy);
        Assert.Equal(AnchorEdges.Left | AnchorEdges.Right, ctrl.Input.Anchors & (AnchorEdges.Left | AnchorEdges.Right));

        const float authoredParentWidth = 490f;
        ctrl.Input.ApplyAnchor(authoredParentWidth, ctrl.Input.Height);

        const float narrowerParentWidth = 300f;
        ctrl.Input.ApplyAnchor(narrowerParentWidth, ctrl.Input.Height);

        Assert.True(
            ctrl.Input.Left + ctrl.Input.Width <= narrowerParentWidth,
            $"input right edge ({ctrl.Input.Left + ctrl.Input.Width}) overflowed the narrower parent width ({narrowerParentWidth})");
    }

    [Fact]
    public void Bind_InputField_WithNoImportedLayoutPolicy_GrowsWithWiderResize()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(ctrl);

        const float authoredParentWidth = 490f;
        ctrl!.Input.ApplyAnchor(authoredParentWidth, ctrl.Input.Height);
        float originalWidth = ctrl.Input.Width;

        const float widerParentWidth = 800f;
        ctrl.Input.ApplyAnchor(widerParentWidth, ctrl.Input.Height);

        Assert.True(ctrl.Input.Width > originalWidth, "the input should widen when the window grows");
        Assert.True(ctrl.Input.Left + ctrl.Input.Width <= widerParentWidth);
    }

    [Fact]
    public void Bind_InputField_WithImportedLayoutPolicy_RightEdgeTracksParentDeltaInsteadOfFreezing()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var inputInfo = FindById(rootInfo, 0x10000016u)
            ?? throw new System.InvalidOperationException("test fixture missing the input node");
        inputInfo.HasOriginalParentSize = true;
        inputInfo.OriginalParentWidth = 490f;
        inputInfo.OriginalParentHeight = 17f;
        inputInfo.Left = 1u;
        inputInfo.Top = 1u;
        inputInfo.Right = 0u;  // far-edge mode this fix must upgrade away from
        inputInfo.Bottom = 1u;

        layout = LayoutImporter.Build(rootInfo, NoTex, null);
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex);

        Assert.NotNull(ctrl);
        Assert.NotNull(ctrl!.Input.LayoutPolicy);
        Assert.Equal(1u, ctrl.Input.LayoutPolicy!.RightMode);

        const float narrowerParentWidth = 300f;
        ctrl.Input.ApplyAnchor(narrowerParentWidth, ctrl.Input.Height);

        Assert.True(
            ctrl.Input.Left + ctrl.Input.Width <= narrowerParentWidth,
            $"input right edge ({ctrl.Input.Left + ctrl.Input.Width}) overflowed the narrower parent width ({narrowerParentWidth})");
    }

    private static ElementInfo? FindById(ElementInfo node, uint id)
    {
        if (node.Id == id) return node;
        foreach (var child in node.Children)
        {
            if (FindById(child, id) is { } found) return found;
        }
        return null;
    }



    [Theory]
    [InlineData(1, 0x10000522u)]
    [InlineData(2, 0x10000523u)]
    [InlineData(3, 0x10000524u)]
    [InlineData(4, 0x10000525u)]
    public void SetIndicatorOpen_Open_SetsHighlightState(int windowId, uint indicatorId)
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex)!;
        var indicator = Assert.IsType<UiButton>(layout.FindElement(indicatorId));

        ctrl.SetIndicatorOpen(windowId, open: true);

        Assert.Equal(UiButtonStateMachine.Highlight, indicator.ActiveRetailStateId);
    }

    [Fact]
    public void SetIndicatorOpen_Closed_SetsNormalState()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex)!;
        var indicator = Assert.IsType<UiButton>(layout.FindElement(0x10000522u));

        ctrl.SetIndicatorOpen(1, open: true);
        ctrl.SetIndicatorOpen(1, open: false);

        Assert.Equal(UiButtonStateMachine.Normal, indicator.ActiveRetailStateId);
    }

    [Fact]
    public void SetIndicatorOpen_DoesNotAffectOtherWindowsIndicators()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex)!;
        var indicator2 = Assert.IsType<UiButton>(layout.FindElement(0x10000523u));

        ctrl.SetIndicatorOpen(1, open: true);

        Assert.Equal(UiButtonStateMachine.Normal, indicator2.ActiveRetailStateId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void SetIndicatorOpen_OutOfRangeWindowId_Throws(int windowId)
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var ctrl = ChatWindowController.Bind(rootInfo, layout, vm, () => bus, new ChatWindowState(), null, null, NoTex)!;

        Assert.Throws<ArgumentOutOfRangeException>(() => ctrl.SetIndicatorOpen(windowId, open: true));
    }

    [Fact]
    public void TalkButton_UsesAuthoredShortCaptions()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        ChatWindowController? ctrl = ChatWindowController.Bind(
            rootInfo, layout, vm, () => NullCommandBus.Instance,
            new ChatWindowState(), null, null, NoTex);
        Assert.NotNull(ctrl);
        UiMenu menu = Assert.IsType<UiMenu>(layout.FindElement(0x10000014u));

        Assert.Equal("Chat", menu.ButtonLabelProvider!());
        menu.OnSelect!.Invoke(ChatChannelKind.General);
        Assert.Equal("Gen", menu.ButtonLabelProvider());
        menu.OnSelect.Invoke(ChatChannelKind.Lfg);
        Assert.Equal("LFG", menu.ButtonLabelProvider());
        menu.OnSelect.Invoke(ChatChannelKind.Trade);
        Assert.Equal("Trade", menu.ButtonLabelProvider());

        // A DAT resolver (production) wins over the fallback.
        var (rootInfo2, layout2, vm2) = BuildTestTree();
        ChatWindowController? ctrl2 = ChatWindowController.Bind(
            rootInfo2, layout2, vm2, () => NullCommandBus.Instance,
            new ChatWindowState(), null, null, NoTex,
            chatStrings: key => key == "ID_Chat_ChatTargetMenuGeneral" ? "LOC" : null);
        Assert.NotNull(ctrl2);
        UiMenu menu2 = Assert.IsType<UiMenu>(layout2.FindElement(0x10000014u));
        menu2.OnSelect!.Invoke(ChatChannelKind.General);
        Assert.Equal("LOC", menu2.ButtonLabelProvider!());
    }
    private static (ChatWindowController Controller, RetailWindowHandle Handle, UiRoot Root, UiButton Toggle)
        MountedChat()
    {
        var info = FixtureLoader.LoadChatInfos();
        var layout = LayoutImporter.Build(info, NoTex, null);
        var vm = new ChatVM(new ChatLog());
        var controller = ChatWindowController.Bind(info, layout, vm,
            () => new CaptureBus(), new ChatWindowState(), null, null, NoTex)!;
        var root = new UiRoot { Width = 800f, Height = 600f };
        var handle = RetailWindowFrame.Mount(root, controller.Root, NoTex,
            new RetailWindowFrame.Options
            {
                WindowName = WindowNames.Chat,
                Chrome = RetailWindowChrome.Imported,
                Left = 10f, Top = 390f,
                ResizeX = true, ResizeY = true,
                DatConstraintSource = controller.DatWindowInfo,
            });
        controller.AttachWindow(handle);
        return (controller, handle, root, Assert.IsType<UiButton>(layout.FindElement(0x1000046Fu)));
    }

    [Fact]
    public void RestoreMaximizedAtNewScreenRecomputesExpansionAndNormalReturnGeometry()
    {
        var (controller, handle, root, toggle) = MountedChat();
        toggle.OnClick!();
        Assert.True(controller.IsMaximized);
        root.Height = 800f;
        controller.RestoreWindowState(new RetainedWindowState(
            Maximized: true, PersistedTop: 580f, PersistedHeight: 120f));
        Assert.True(controller.IsMaximized);
        Assert.Equal(180f, handle.Top);
        Assert.Equal(520f, handle.Height);
        Assert.Equal(580f, controller.CaptureWindowState().PersistedTop);
        Assert.Equal(120f, controller.CaptureWindowState().PersistedHeight);
        toggle.OnClick!();
        Assert.False(controller.IsMaximized);
        Assert.Equal(580f, handle.Top);
        Assert.Equal(120f, handle.Height);
    }

    [Fact]
    public void RestoringNormalGeometryWhileMaximizedDoesNotRestorePreviousCharactersPosition()
    {
        var (controller, handle, _, toggle) = MountedChat();
        toggle.OnClick!();
        controller.RestoreWindowState(new RetainedWindowState(
            Maximized: false, PersistedTop: 40f, PersistedHeight: 150f));
        Assert.False(controller.IsMaximized);
        Assert.Equal(40f, handle.Top);
        Assert.Equal(150f, handle.Height);
        toggle.OnClick!();
        toggle.OnClick!();
        Assert.Equal(40f, handle.Top);
        Assert.Equal(150f, handle.Height);
    }

    [Fact]
    public void RestoringMaximizedFromNormalExpandsFromSuppliedNormalGeometry()
    {
        var (controller, handle, _, toggle) = MountedChat();
        controller.RestoreWindowState(new RetainedWindowState(
            Maximized: true, PersistedTop: 350f, PersistedHeight: 150f));
        Assert.True(controller.IsMaximized);
        Assert.Equal(50f, handle.Top);
        Assert.Equal(450f, handle.Height);
        toggle.OnClick!();
        Assert.Equal(350f, handle.Top);
        Assert.Equal(150f, handle.Height);
    }}
