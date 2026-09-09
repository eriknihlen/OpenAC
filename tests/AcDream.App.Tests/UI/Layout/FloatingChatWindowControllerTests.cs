using System.Collections.Generic;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI.Layout;

public class FloatingChatWindowControllerTests
{
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
            Id = 0x10000011u, Type = 12,
            X = 5, Y = 20, Width = 224, Height = 60,
        };
        var trackNode = new ElementInfo
        {
            Id = 0x10000012u, Type = 3,
            X = 229, Y = 20, Width = 16, Height = 60,
        };
        var transcriptPanel = new ElementInfo
        {
            Id = 0x10000010u, Type = 3, X = 0, Y = 20, Width = 250, Height = 60,
        };
        transcriptPanel.Children.Add(transcriptNode);
        transcriptPanel.Children.Add(trackNode);

        var inputNode = new ElementInfo
        {
            Id = 0x10000016u, Type = 12,
            X = 0, Y = 80, Width = 202, Height = 18,
        };
        var inputState = new UiStateInfo { Id = UiStateInfo.DirectStateId };
        inputState.Properties.Values[0x16u] = new UiPropertyValue { Kind = UiPropertyKind.Bool, BoolValue = true };
        inputState.Properties.Values[0x20u] = new UiPropertyValue { Kind = UiPropertyKind.Bool, BoolValue = true };
        inputState.Properties.Values[0x27u] = new UiPropertyValue { Kind = UiPropertyKind.Bool, BoolValue = true };
        inputNode.States[UiStateInfo.DirectStateId] = inputState;
        var sendNode = new ElementInfo
        {
            Id = 0x10000019u, Type = 3, X = 202, Y = 80, Width = 38, Height = 18,
        };
        var inputRow = new ElementInfo
        {
            Id = 0x10000509u, Type = 3, X = 0, Y = 80, Width = 250, Height = 18,
        };
        inputRow.Children.Add(inputNode);
        inputRow.Children.Add(sendNode);

        var titleBar = new ElementInfo { Id = 0x100004D9u, Type = 3, X = 0, Y = 0, Width = 240, Height = 16 };
        var closeButton = new ElementInfo { Id = 0x1000052Au, Type = 3, X = 225, Y = 1, Width = 14, Height = 14 };

        var root = new ElementInfo { Id = 0x100004F7u, Type = 3, Width = 250, Height = 108 };
        root.Children.Add(transcriptPanel);
        root.Children.Add(inputRow);
        root.Children.Add(titleBar);
        root.Children.Add(closeButton);

        var layout = LayoutImporter.Build(root, NoTex, null);
        var vm = new ChatVM(log ?? new ChatLog());
        return (root, layout, vm);
    }

    // ── Bind smoke tests ──────────────────────────────────────────────────

    [Fact]
    public void Bind_Returns_NonNull_OnValidTree()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            1, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);

        Assert.NotNull(ctrl);
        Assert.Equal(1, ctrl!.WindowId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void Bind_InvalidWindowId_Throws(int windowId)
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FloatingChatWindowController.Bind(
                windowId, rootInfo, layout, vm, () => bus, filters, null, null, NoTex));
    }

    [Fact]
    public void Bind_Returns_Null_WhenTranscriptPanelMissing()
    {
        var root = new ElementInfo { Id = 0x100004F7u, Type = 3, Width = 250, Height = 108 };
        var layout = LayoutImporter.Build(root, NoTex, null);
        var vm = new ChatVM(new ChatLog());
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            1, root, layout, vm, () => bus, filters, null, null, NoTex);

        Assert.Null(ctrl);
    }

    [Fact]
    public void Bind_Transcript_IsChildOfTranscriptPanel()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            2, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);

        Assert.NotNull(ctrl);
        var panel = layout.FindElement(0x10000010u);
        Assert.NotNull(panel);
        Assert.Contains(ctrl!.Transcript, panel!.Children);
    }

    [Fact]
    public void Bind_Input_IsChildOfFloatyInputRow_NotMainWindowInputBar()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            3, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);

        Assert.NotNull(ctrl);
        var row = layout.FindElement(0x10000509u);
        Assert.NotNull(row);
        Assert.Contains(ctrl!.Input, row!.Children);
    }


    [Fact]
    public void Bind_InputSubmit_AlwaysSendsOnSayChannel()
    {
        var (rootInfo, layout, vm) = BuildTestTree();
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            4, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);

        Assert.NotNull(ctrl);
        ctrl!.Input.OnSubmit!.Invoke("hi everyone");

        var cmd = Assert.IsType<SendChatCmd>(Assert.Single(bus.Published));
        Assert.Equal(ChatChannelKind.Say, cmd.Channel);
        Assert.Equal("hi everyone", cmd.Text);
    }

    // ── Display rule matrix: this window's filter accepts/rejects a line ───

    [Fact]
    public void Transcript_ShowsOnlyLinesThisWindowsFilterAccepts()
    {
        var log = new ChatLog();
        var (rootInfo, layout, vm) = BuildTestTree(log);
        var bus = new CaptureBus();
        var filters = new ChatWindowState(); // window 1 default: Speech/Tell/SpeechDirectSend/Emote

        var ctrl = FloatingChatWindowController.Bind(
            1, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);
        Assert.NotNull(ctrl);

        log.OnSystemMessage("visible speech", chatType: 0x02u);   // Speech — in window 1's filter
        log.OnSystemMessage("hidden social", chatType: 0x0Au);    // Social — NOT in window 1's filter

        var lines = ctrl!.Transcript.LinesProvider();

        Assert.Single(lines);
        Assert.Equal("visible speech", lines[0].Text);
    }

    [Fact]
    public void Transcript_DifferentWindow_AcceptsADifferentSubsetOfTypes()
    {
        var log = new ChatLog();
        var (rootInfo, layout, vm) = BuildTestTree(log);
        var bus = new CaptureBus();
        var filters = new ChatWindowState(); // window 3 default: Fellowship only

        var ctrl = FloatingChatWindowController.Bind(
            3, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);
        Assert.NotNull(ctrl);

        log.OnSystemMessage("speech line", chatType: 0x02u);       // Speech — not window 3's filter
        log.OnSystemMessage("fellowship line", chatType: 0x13u);   // Fellowship — window 3's filter

        var lines = ctrl!.Transcript.LinesProvider();

        Assert.Single(lines);
        Assert.Equal("fellowship line", lines[0].Text);
    }

    [Fact]
    public void Transcript_FilterChange_IsReflectedOnNextRebuild()
    {
        var log = new ChatLog();
        var (rootInfo, layout, vm) = BuildTestTree(log);
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            2, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);
        Assert.NotNull(ctrl);

        log.OnSystemMessage("speech line", chatType: 0x02u); // Speech — not window 2's default filter
        Assert.Empty(ctrl!.Transcript.LinesProvider());

        filters.SetFilter(2, filters.GetFilter(2) | (1UL << 0x02));

        var lines = ctrl.Transcript.LinesProvider();
        Assert.Single(lines);
        Assert.Equal("speech line", lines[0].Text);
    }

    [Fact]
    public void Transcript_UnchangedFilterAndContent_ReusesCachedLayout()
    {
        var log = new ChatLog();
        var (rootInfo, layout, vm) = BuildTestTree(log);
        var bus = new CaptureBus();
        var filters = new ChatWindowState();

        var ctrl = FloatingChatWindowController.Bind(
            1, rootInfo, layout, vm, () => bus, filters, null, null, NoTex);
        Assert.NotNull(ctrl);

        log.OnSystemMessage("speech line", chatType: 0x02u);
        var first = ctrl!.Transcript.LinesProvider();
        var unchanged = ctrl.Transcript.LinesProvider();

        Assert.Same(first, unchanged);
        Assert.Equal(1, ctrl.TranscriptLayoutBuildCount);
    }
}
