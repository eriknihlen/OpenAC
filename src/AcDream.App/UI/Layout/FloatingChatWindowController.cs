using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.UI.Layout;

public sealed class FloatingChatWindowController : IRetainedPanelController
{
    public const uint LayoutId = 0x2100005Bu;

    private const uint RootId            = 0x100004F7u;   // window root, 250x108
    private const uint TranscriptPanelId = 0x10000010u;
    private const uint TranscriptId      = 0x10000011u;   // Type-12 prototype — skipped by factory
    private const uint TrackId           = 0x10000012u;
    private const uint InputRowId        = 0x10000509u;
    private const uint InputId           = 0x10000016u;   // Type-12 Text + Editable 0x16 -> UiField
    private const uint SendId            = 0x10000019u;
    private const uint TitleBarId        = 0x100004D9u;
    private const uint CloseButtonId     = 0x1000052Au;

    private bool _disposed;

    public int WindowId { get; }

    public UiElement Root { get; private set; } = null!;
    public UiText Transcript { get; private set; } = null!;
    public UiField Input { get; private set; } = null!;
    public UiScrollbar? Scrollbar { get; private set; }

    public ElementInfo DatWindowInfo { get; private set; } = null!;

    public RetailWindowHandle? WindowHandle { get; private set; }

    private IReadOnlyList<UiText.Line> _cachedTranscriptLines = Array.Empty<UiText.Line>();
    private long _cachedTranscriptRevision = -1;
    private ulong _cachedFilter;
    private float _cachedTranscriptWrapWidth = float.NaN;
    private UiDatFont? _cachedTranscriptDatFont;
    private BitmapFont? _cachedTranscriptDebugFont;
    internal int TranscriptLayoutBuildCount { get; private set; }

    private FloatingChatWindowController(int windowId)
    {
        WindowId = windowId;
    }

    public static FloatingChatWindowController? Bind(
        int windowId,
        ElementInfo rootInfo,
        ImportedLayout layout,
        ChatVM vm,
        Func<ICommandBus> busProvider,
        ChatWindowState windowFilters,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        Func<uint, (uint tex, int w, int h)> resolve)
    {
        if (windowId < ChatWindowState.MinFloatingWindowId || windowId > ChatWindowState.MaxFloatingWindowId)
            throw new ArgumentOutOfRangeException(nameof(windowId));
        ArgumentNullException.ThrowIfNull(windowFilters);

        var transcriptPanel = layout.FindElement(TranscriptPanelId);
        var inputRow        = layout.FindElement(InputRowId);
        var input           = layout.FindElement(InputId) as UiField;

        if (input is null || transcriptPanel is null || inputRow is null)
        {
            Console.WriteLine(
                $"[UI] FloatingChatWindowController.Bind(window {windowId}): missing required elements " +
                $"(input={input is not null}, panel={transcriptPanel is not null}, row={inputRow is not null}) — " +
                $"floating chat window will not be interactive.");
            return null;
        }

        var window = layout.FindElement(RootId) ?? layout.Root;
        var c = new FloatingChatWindowController(windowId)
        {
            Root = window,
            DatWindowInfo = FindInfo(rootInfo, RootId) ?? rootInfo,
        };

        // ── Transcript ───────────────────────────────────────────────────
        c.Transcript = layout.FindElement(TranscriptId) as UiText
            ?? throw new InvalidOperationException("floating chat transcript 0x10000011 not built as UiText");
        c.Transcript.DatFont = datFont;
        c.Transcript.Font    = debugFont;
        c.Transcript.Centered = false;
        c.Transcript.RightAligned = false;
        c.Transcript.OneLine = false;
        c.Transcript.Selectable = true;
        c.Transcript.LinesProvider = () => c.GetTranscriptLines(vm, windowFilters);

        c.Input = input;
        c.Input.DatFont = datFont;
        c.Input.Font = debugFont;
        c.Input.SpriteResolve = resolve;
        c.Input.OnSubmit = text => ChatCommandRouter.Submit(text, vm, busProvider(), ChatChannelKind.Say);

        if (c.Input.LayoutPolicy is { } inputPolicy)
        {
            c.Input.LayoutPolicy = new UiLayoutPolicy(
                inputPolicy.LeftMode,
                inputPolicy.TopMode,
                rightMode: 1u,
                inputPolicy.BottomMode,
                inputPolicy.OriginalChild,
                inputPolicy.OriginalParent);
        }
        else
        {
            c.Input.Anchors |= AnchorEdges.Right;
        }

        // ── Scrollbar — bind the factory-built Type-11 track element ────
        if (layout.FindElement(TrackId) is UiScrollbar bar)
        {
            bar.Model = c.Transcript.Scroll;
            bar.SpriteResolve ??= resolve;
            c.Scrollbar = bar;
        }

        // ── Send button ─────────────────────────────────────────────────
        if (layout.FindElement(SendId) is UiButton sendEl)
        {
            sendEl.OnClick = () => c.Input.Submit();
            sendEl.Label = "Send";
            sendEl.LabelFont = datFont;
            sendEl.LabelColor = new Vector4(1f, 0.92f, 0.72f, 1f);
        }

        if (layout.FindElement(TitleBarId) is UiText titleText)
        {
            titleText.DatFont = datFont;
            titleText.Font = debugFont;
            titleText.OneLine = true;
            string title = $"Chat {windowId}";
            var titleColor = new Vector4(1f, 0.92f, 0.72f, 1f);
            titleText.LinesProvider = () => new[] { new UiText.Line(title, titleColor) };
        }

        if (layout.FindElement(CloseButtonId) is UiButton closeEl)
        {
            closeEl.OnClick = () => c.WindowHandle?.Hide();
        }

        return c;
    }

    public void AttachWindow(RetailWindowHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!ReferenceEquals(handle.ContentRoot, Root))
            throw new ArgumentException(
                "Floating chat handle content root does not match the bound layout.", nameof(handle));
        if (WindowHandle is not null && !ReferenceEquals(WindowHandle, handle))
            throw new InvalidOperationException("Floating chat controller is already attached to another window.");
        WindowHandle = handle;
    }

    private static ElementInfo? FindInfo(ElementInfo node, uint id)
    {
        if (node.Id == id) return node;
        foreach (ElementInfo child in node.Children)
        {
            ElementInfo? found = FindInfo(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private IReadOnlyList<UiText.Line> GetTranscriptLines(ChatVM vm, ChatWindowState windowFilters)
    {
        float maxW = Transcript.Width - 2f * Transcript.Padding;
        UiDatFont? datFont = Transcript.DatFont;
        BitmapFont? debugFont = Transcript.Font;
        long revision = vm.Revision;
        ulong filter = windowFilters.GetFilter(WindowId);

        if (_cachedTranscriptRevision == revision
            && _cachedFilter == filter
            && _cachedTranscriptWrapWidth.Equals(maxW)
            && ReferenceEquals(_cachedTranscriptDatFont, datFont)
            && ReferenceEquals(_cachedTranscriptDebugFont, debugFont))
        {
            return _cachedTranscriptLines;
        }

        var detailed = vm.RecentLinesDetailed();
        Func<string, float> measure =
              datFont   is { } df ? s => df.MeasureWidth(s)
            : debugFont is { } bf ? s => bf.MeasureWidth(s)
            : static s => s.Length * 7f;

        bool Accept(uint logTextType) => windowFilters.ShouldDisplay(
            WindowId, ChatWindowState.BroadcastTargetWindow, logTextType);
        var result = ChatTranscriptRenderer.BuildLines(
            detailed, maxW, measure, Accept, Transcript.DefaultColor);

        _cachedTranscriptRevision = revision;
        _cachedFilter = filter;
        _cachedTranscriptWrapWidth = maxW;
        _cachedTranscriptDatFont = datFont;
        _cachedTranscriptDebugFont = debugFont;
        _cachedTranscriptLines = result;
        TranscriptLayoutBuildCount++;
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
