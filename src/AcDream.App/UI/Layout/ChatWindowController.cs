using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.Rendering;
using AcDream.App.UI;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions;
using AcDream.UI.Abstractions.Input;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.UI.Layout;

public sealed class ChatWindowController : IRetainedWindowStateController, IRetainedPanelController
{
    public const uint LayoutId = 0x2100006Fu;
    private bool _disposed;

    private const uint RootId         = 0x10000600u;
    private const uint TranscriptPanelId = 0x10000010u;
    private const uint TranscriptId   = 0x10000011u;   // Type-12 prototype — skipped by factory

    private const uint UnreadIndicatorId = 0x1000048Cu;
    private const uint TrackId        = 0x10000012u;
    private const uint InputBarId     = 0x10000013u;
    private const uint MenuId         = 0x10000014u;
    private const uint MenuLabelId    = 0x10000015u;
    private const uint InputId        = 0x10000016u;   // Type-12 Text + Editable 0x16 → UiField
    private const uint SendId         = 0x10000019u;
    private const uint MaxMinId       = 0x1000046Fu;

    private const uint Indicator1Id = 0x10000522u;
    private const uint Indicator2Id = 0x10000523u;
    private const uint Indicator3Id = 0x10000524u;
    private const uint Indicator4Id = 0x10000525u;

    private static readonly uint[] LockedTwinIds =
    {
        0x10000693u, 0x10000694u, 0x10000695u, 0x10000696u,
        0x10000697u, 0x10000698u, 0x10000699u, 0x1000069Au,
    };

    private const uint MenuNormal       = 0x06004D65u;   // button face
    private const uint MenuPressed      = 0x06004D66u;   // button pressed
    private const uint MenuPopupBg      = 0x0600124Cu;
    private const uint MenuItemRow      = 0x0600124Eu;
    private const uint MenuItemSelected = 0x0600124Du;

    // ── Public surface ─────────────────────────────────────────────────────

    public UiElement Root { get; private set; } = null!;

    public UiText Transcript { get; private set; } = null!;

    public UiField Input { get; private set; } = null!;

    public UiScrollbar Scrollbar { get; private set; } = null!;

    public UiMenu Menu { get; private set; } = null!;

    public ElementInfo DatWindowInfo { get; private set; } = null!;

    public RetailWindowHandle? WindowHandle { get; private set; }
    public bool IsMaximized => _maximized;

    // ── Private state ──────────────────────────────────────────────────────

    private ChatChannelKind _activeChannel = ChatChannelKind.Say;

    private ChatWindowState _windowFilters = null!;

    private IReadOnlyList<UiText.Line> _cachedTranscriptLines = Array.Empty<UiText.Line>();

    private readonly List<IReadOnlyList<UiText.TextRun>?> _cachedTranscriptRuns = new();

    /// <summary>Line identities parallel to <see cref="_cachedTranscriptLines"/>, so a text
    /// selection stays on the message it was made on as the transcript moves under it.</summary>
    private readonly List<UiText.LineKey> _cachedTranscriptLineKeys = new();

    private readonly List<IReadOnlyList<(int Start, int Length, ChatTextTag Tag)>?>
        _cachedTranscriptTags = new();

    private UiElement? _unreadIndicator;

    /// <summary>Test seam: the bound unseen-text indicator, if the layout has one.</summary>
    internal UiElement? UnreadIndicatorForTest => _unreadIndicator;

    /// <summary>Test seam: force the unseen flag without faking a line arrival.</summary>
    internal void SetUnreadForTest(bool unread) => _hasUnseenText = unread;

    private bool _hasUnseenText;
    private long _cachedTranscriptRevision = -1;
    private ulong _cachedFilter;
    private float _cachedTranscriptWrapWidth = float.NaN;
    private UiDatFont? _cachedTranscriptDatFont;
    private BitmapFont? _cachedTranscriptDebugFont;
    internal int TranscriptLayoutBuildCount { get; private set; }


    private enum TalkFocusSpecial
    {
        Squelch,
        TellToSelected,
    }

    private string? _tellTarget;

    private Func<string, string?>? _chatStrings;

    private string S(string key, string authoredFallback)
        => _chatStrings?.Invoke(key) ?? authoredFallback;

    private static readonly (string Key, string Fallback, ChatChannelKind Channel)[] ChannelItems =
    {
        ("ID_Chat_TellToAll",      "Chat to All",           ChatChannelKind.Say),
        ("ID_Chat_TellToFellows",  "Tell to Fellows",       ChatChannelKind.Fellowship),
        ("ID_Chat_TellToGeneral",  "Tell to General Chat",  ChatChannelKind.General),
        ("ID_Chat_TellToLFG",      "Tell to LFG Chat",      ChatChannelKind.Lfg),
        ("ID_Chat_TellToSociety",  "Tell to Society Chat",  ChatChannelKind.Society),
        ("ID_Chat_TellToMonarch",  "Tell to Monarch",       ChatChannelKind.Monarch),
        ("ID_Chat_TellToPatron",   "Tell to Patron",        ChatChannelKind.Patron),
        ("ID_Chat_TellToVassals",  "Tell to Vassals",       ChatChannelKind.Vassals),
        ("ID_Chat_TellToAllegiance", "Tell to Allegiance",  ChatChannelKind.Allegiance),
        ("ID_Chat_TellToTrade",    "Tell to Trade Chat",    ChatChannelKind.Trade),
        ("ID_Chat_TellToRoleplay", "Tell to Roleplay Chat", ChatChannelKind.Roleplay),
        ("ID_Chat_TellToOlthoi",   "Tell to Olthoi Chat",   ChatChannelKind.Olthoi),
    };

    private string ChannelButtonLabel(ChatChannelKind k) => k switch
    {
        ChatChannelKind.Say        => S("ID_Chat_ChatTargetMenu", "Chat"),
        ChatChannelKind.Tell       => S("ID_Chat_ChatTargetMenuSelected", "Tell"),
        ChatChannelKind.General    => S("ID_Chat_ChatTargetMenuGeneral", "Gen"),
        ChatChannelKind.Trade      => S("ID_Chat_ChatTargetMenuTrade", "Trade"),
        ChatChannelKind.Lfg        => S("ID_Chat_ChatTargetMenuLFG", "LFG"),
        ChatChannelKind.Fellowship => S("ID_Chat_ChatTargetMenuFellows", "Fell"),
        ChatChannelKind.Allegiance => S("ID_Chat_ChatTargetMenuAllegiance", "Alg"),
        ChatChannelKind.Patron     => S("ID_Chat_ChatTargetMenuPatron", "Pat"),
        ChatChannelKind.Vassals    => S("ID_Chat_ChatTargetMenuVassals", "Vas"),
        ChatChannelKind.Monarch    => S("ID_Chat_ChatTargetMenuMonarch", "Mon"),
        ChatChannelKind.Roleplay   => S("ID_Chat_ChatTargetMenuRoleplay", "RP"),
        ChatChannelKind.Society    => S("ID_Chat_ChatTargetMenuSociety", "Soc"),
        ChatChannelKind.Olthoi     => S("ID_Chat_ChatTargetMenuOlthoi", "Olt"),
        _                          => S("ID_Chat_ChatTargetMenu", "Chat"),
    };

    private static bool ChannelAvailable(ChatChannelKind k)
        => k is ChatChannelKind.Say or ChatChannelKind.General or ChatChannelKind.Trade or ChatChannelKind.Lfg;

    /// <summary>Window height before maximize (stored to restore on un-maximize).</summary>
    private float _normalHeight;
    /// <summary>Window top before maximize.</summary>
    private float _normalTop;
    private bool _maximized;
    private UiButton? _maxMinButton;

    private readonly UiButton?[] _indicatorButtons = new UiButton?[4];

    // ── Factory ────────────────────────────────────────────────────────────

    public static ChatWindowController? Bind(
        ElementInfo rootInfo,
        ImportedLayout layout,
        ChatVM vm,
        Func<ICommandBus> busProvider,
        ChatWindowState windowFilters,
        UiDatFont? datFont,
        BitmapFont? debugFont,
        Func<uint, (uint tex, int w, int h)> resolve,
        Func<string?>? selectedTargetName = null,
        Func<string, string?>? chatStrings = null,
        Func<uint, UiDatFont?>? resolveFont = null)
    {
        ArgumentNullException.ThrowIfNull(windowFilters);

        // Their parent panels must exist as real widgets in the layout tree.
        var transcriptPanel = layout.FindElement(TranscriptPanelId);
        var inputBar        = layout.FindElement(InputBarId);
        var input           = layout.FindElement(InputId) as UiField;

        if (input is null || transcriptPanel is null || inputBar is null)
        {
            Console.WriteLine(
                $"[UI] ChatWindowController.Bind: missing required elements " +
                $"(input={input is not null}, " +
                $"panel={transcriptPanel is not null}, bar={inputBar is not null}) — " +
                $"chat window will not be interactive.");
            return null;
        }

        var window = layout.FindElement(RootId) ?? layout.Root;
        var c = new ChatWindowController
        {
            Root = window,
            DatWindowInfo = FindInfo(rootInfo, RootId) ?? rootInfo,
            _windowFilters = windowFilters,
            _chatStrings = chatStrings,
        };

        foreach (uint id in LockedTwinIds)
            if (layout.FindElement(id) is { } twin)
                twin.Visible = false;

        uint[] indicatorIds = { Indicator1Id, Indicator2Id, Indicator3Id, Indicator4Id };
        for (int i = 0; i < indicatorIds.Length; i++)
        {
            var indicator = layout.FindElement(indicatorIds[i]) as UiButton;
            if (indicator is not null)
                indicator.SuppressSelfToggle = true;
            c._indicatorButtons[i] = indicator;
        }

        c.Transcript = layout.FindElement(TranscriptId) as UiText
            ?? throw new InvalidOperationException("chat transcript 0x10000011 not built as UiText");
        c.Transcript.DatFont = datFont;
        c.Transcript.Font    = debugFont;
        c.Transcript.Centered = false;
        c.Transcript.RightAligned = false;
        c.Transcript.OneLine = false;
        c.Transcript.Selectable = true;
        c.Transcript.LinesProvider   = () => c.GetTranscriptLines(vm);
        c.Transcript.LineKeysProvider = () => c._cachedTranscriptLineKeys;
        c.Transcript.LineRunsProvider = index =>
            index >= 0 && index < c._cachedTranscriptRuns.Count
                ? c._cachedTranscriptRuns[index]
                : null;
        c.Transcript.OnCharClick = pos => c.TryStartTellFromTag(pos);

        // ── Unread indicator ─────────────────────────────────────────────
        c._unreadIndicator = layout.FindElement(UnreadIndicatorId);
        if (c._unreadIndicator is not null)
        {
            c.SetUnreadIndicatorState(unread: false);
            if (c._unreadIndicator is UiButton unread)
                unread.OnClick = c.ScrollToNewestAndClearUnread;
        }

        c.Input = input;
        c.Input.DatFont = datFont;
        c.Input.Font = debugFont;
        c.Input.SpriteResolve = resolve;
        c.Input.TextReplacer = text =>
            ChatTextReplacements.Expand(text, vm.LastIncomingTellSender);
        c.Input.OnSubmit = text => ChatCommandRouter.Submit(
            text, vm, busProvider(), c._activeChannel, c._tellTarget);

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

        var track = layout.FindElement(TrackId);
        if (track is UiScrollbar bar)
        {
            bar.Model         = c.Transcript.Scroll;
            bar.SpriteResolve ??= resolve;
            c.Scrollbar = bar;
        }

        if (layout.FindElement(MenuId) is UiMenu menu)
        {
            menu.DatFont = datFont; menu.Font = debugFont; menu.SpriteResolve = resolve;
            if (FindInfo(rootInfo, MenuLabelId) is { } labelInfo)
            {
                if (labelInfo.FontColor is { } authoredColor)
                    menu.TextColor = authoredColor;
                if (labelInfo.FontDid != 0u
                    && resolveFont?.Invoke(labelInfo.FontDid) is { } buttonFont)
                    menu.ButtonDatFont = buttonFont;
                menu.ButtonTextCentered =
                    labelInfo.HJustify == HJustify.Center;
            }
            menu.NormalSprite = MenuNormal; menu.PressedSprite = MenuPressed;
            menu.PopupBgSprite = MenuPopupBg;
            menu.ItemNormalSprite = MenuItemRow; menu.ItemHighlightSprite = MenuItemSelected;
            string? SelectedName() => selectedTargetName?.Invoke();

            void RebuildItems()
            {
                string? target = SelectedName();
                var items = new List<UiMenu.MenuItem>(ChannelItems.Length)
                {
                    new(target is null
                            ? c.S("ID_Chat_SquelchSelectedNoSelection",
                                  "Squelch (ignore) Selected")
                            : c.S("ID_Chat_SquelchSelected", "Squelch (ignore) ") + target,
                        TalkFocusSpecial.Squelch),
                    new(target is null
                            ? c.S("ID_Chat_TellToSelectedNoSelection", "Tell to Selected")
                            : c.S("ID_Chat_TellToSelected", "Tell to ") + target,
                        TalkFocusSpecial.TellToSelected),
                };
                foreach ((string key, string fallback, ChatChannelKind ch) in ChannelItems)
                    items.Add(new UiMenu.MenuItem(c.S(key, fallback), ch));
                menu.Items = items.ToArray();
            }

            RebuildItems();
            menu.Selected = (object?)c._activeChannel;
            menu.EnabledProvider = p => p switch
            {
                ChatChannelKind ch => ChannelAvailable(ch),
                TalkFocusSpecial => SelectedName() is not null,
                _ => true,
            };
            menu.ButtonLabelProvider = () => c.ChannelButtonLabel(c._activeChannel);
            menu.OnOpen = RebuildItems;
            menu.OnSelect = p =>
            {
                switch (p)
                {
                    case ChatChannelKind ch:
                        c._activeChannel = ch;
                        c._tellTarget = null;
                        menu.Selected = p;
                        break;

                    case TalkFocusSpecial.TellToSelected when SelectedName() is { } name:
                        c._activeChannel = ChatChannelKind.Tell;
                        c._tellTarget = name;
                        menu.Selected = p;
                        break;

                    case TalkFocusSpecial.Squelch when SelectedName() is { } squelched:
                        busProvider().Publish(
                            new ExecuteClientCommandCmd(
                                ClientCommandId.Squelch, squelched));
                        break;
                }
            };
            c.Menu = menu;
        }

        if (layout.FindElement(SendId) is UiButton sendEl)
        {
            sendEl.OnClick = () => c.Input.Submit();
            sendEl.Label = "Send";
            ElementInfo? sendInfo = FindInfo(rootInfo, SendId);
            sendEl.LabelFont =
                (sendInfo?.FontDid is { } sendFontDid and not 0u
                    ? resolveFont?.Invoke(sendFontDid)
                    : null) ?? datFont;
            sendEl.LabelColor = sendInfo?.FontColor ?? new Vector4(1f, 1f, 1f, 1f);
        }


        if (layout.FindElement(MaxMinId) is UiButton maxMinEl)
        {
            c._maxMinButton = maxMinEl;
            maxMinEl.OnClick = c.ToggleMaximize;
        }

        return c;
    }

    // ── Max/min implementation ─────────────────────────────────────────────

    public void AttachWindow(RetailWindowHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!ReferenceEquals(handle.ContentRoot, Root))
            throw new ArgumentException("Chat handle content root does not match the bound layout.", nameof(handle));
        if (WindowHandle is not null && !ReferenceEquals(WindowHandle, handle))
            throw new InvalidOperationException("Chat controller is already attached to another window.");
        WindowHandle = handle;
    }

    private void ToggleMaximize()
    {
        if (WindowHandle is not { IsRegistered: true } handle)
            return;

        UiElement frame = handle.OuterFrame;
        float parentHeight = frame.Parent?.Height ?? 0f;
        if (parentHeight <= 0f)
            return;

        if (_maximized)
        {
            float restoredHeight = Math.Clamp(_normalHeight, frame.MinHeight, frame.MaxHeight);
            float restoredTop = Math.Clamp(
                _normalTop,
                0f,
                MathF.Max(0f, parentHeight - frame.MinHeight));
            _maximized = false;
            _maxMinButton?.TrySetRetailState(RetailUiStateIds.Minimized);
            handle.ResizeTo(frame.Width, restoredHeight);
            handle.MoveTo(frame.Left, restoredTop);
            return;
        }

        _normalTop = frame.Top;
        _normalHeight = frame.Height;

        float expansion = parentHeight / 2f;
        float targetHeight = Math.Clamp(
            MathF.Min(frame.Height + expansion, parentHeight),
            frame.MinHeight,
            frame.MaxHeight);
        bool growUp = frame.Top + targetHeight > parentHeight
                      || frame.Top >= parentHeight / 2f;
        float targetTop = growUp
            ? MathF.Max(0f, frame.Top - (targetHeight - frame.Height))
            : frame.Top;
        targetHeight = MathF.Min(targetHeight, parentHeight - targetTop);

        _maximized = true;
        _maxMinButton?.TrySetRetailState(RetailUiStateIds.Maximized);
        handle.ResizeTo(frame.Width, targetHeight);
        handle.MoveTo(frame.Left, targetTop);
    }

    public void SetIndicatorOpen(int windowId, bool open)
    {
        if (windowId < 1 || windowId > _indicatorButtons.Length)
            throw new ArgumentOutOfRangeException(nameof(windowId));
        _indicatorButtons[windowId - 1]?.TrySetRetailState(
            open ? UiButtonStateMachine.Highlight : UiButtonStateMachine.Normal);
    }

    public void BindIndicatorClicks(Func<int, bool> toggleFloatingWindow)
    {
        ArgumentNullException.ThrowIfNull(toggleFloatingWindow);
        for (int i = 0; i < _indicatorButtons.Length; i++)
        {
            int windowId = i + 1;
            if (_indicatorButtons[i] is { } indicator)
                indicator.OnClick = () => toggleFloatingWindow(windowId);
        }
    }

    public RetainedWindowState CaptureWindowState()
        => new(
            Maximized: _maximized,
            PersistedTop: _maximized ? _normalTop : null,
            PersistedHeight: _maximized ? _normalHeight : null);

    public void RestoreWindowState(RetainedWindowState state)
    {
        if ((state.PersistedTop.HasValue || state.PersistedHeight.HasValue)
            && WindowHandle is { IsRegistered: true } handle)
        {
            _maximized = false;
            handle.ResizeTo(handle.Width, state.PersistedHeight ?? handle.Height);
            handle.MoveTo(handle.Left, state.PersistedTop ?? handle.Top);
            _normalTop = handle.Top;
            _normalHeight = handle.Height;
        }

        if (state.Maximized != _maximized)
            ToggleMaximize();
        else
            _maxMinButton?.TrySetRetailState(
                _maximized ? RetailUiStateIds.Maximized : RetailUiStateIds.Minimized);
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

    // ── Helpers ────────────────────────────────────────────────────────────

    private IReadOnlyList<UiText.Line> GetTranscriptLines(ChatVM vm)
    {
        float maxW = Transcript.Width - 2f * Transcript.Padding;
        UiDatFont? datFont = Transcript.DatFont;
        BitmapFont? debugFont = Transcript.Font;
        long revision = vm.Revision;
        ulong filter = _windowFilters.GetFilter(ChatWindowState.MainWindowId);

        if (_cachedTranscriptRevision == revision
            && _cachedFilter == filter
            && _cachedTranscriptWrapWidth.Equals(maxW)
            && ReferenceEquals(_cachedTranscriptDatFont, datFont)
            && ReferenceEquals(_cachedTranscriptDebugFont, debugFont))
        {
            return _cachedTranscriptLines;
        }

        if (_cachedTranscriptRevision != revision
            && _cachedTranscriptRevision >= 0
            && !Transcript.Scroll.AtEnd)
        {
            _hasUnseenText = true;
        }

        var detailed = vm.RecentLinesDetailed();
        if (detailed.Count == 0)
        {
            _cachedTranscriptRuns.Clear();
            _cachedTranscriptTags.Clear();
            _cachedTranscriptLineKeys.Clear();
            return StoreTranscriptLayout(
                Array.Empty<UiText.Line>(), revision, filter, maxW, datFont, debugFont);
        }

        Func<string, float> measure =
              datFont   is { } df ? s => df.MeasureWidth(s)
            : debugFont is { } bf ? s => bf.MeasureWidth(s)
            : static s => s.Length * 7f;

        bool Accept(uint logTextType) => _windowFilters.ShouldDisplay(
            ChatWindowState.MainWindowId, ChatWindowState.BroadcastTargetWindow, logTextType);
        _cachedTranscriptRuns.Clear();
        _cachedTranscriptTags.Clear();
        var result = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW,
            measure,
            Accept,
            Transcript.DefaultColor,
            Transcript.TagColor,
            _cachedTranscriptRuns,
            _cachedTranscriptTags,
            _cachedTranscriptLineKeys);
        return StoreTranscriptLayout(result, revision, filter, maxW, datFont, debugFont);
    }

    internal bool TryStartTellFromTag(UiText.Pos position)
    {
        if (position.Line < 0 || position.Line >= _cachedTranscriptTags.Count)
            return false;
        if (_cachedTranscriptTags[position.Line] is not { } ranges)
            return false;

        foreach ((int start, int length, ChatTextTag tag) in ranges)
        {
            if (position.Col < start || position.Col >= start + length)
                continue;
            if (!tag.TryGetIidString(out _, out string name) || name.Length == 0)
                continue;

            StartTell(name);
            return true;
        }

        return false;
    }

    internal void ScrollToNewestAndClearUnread()
    {
        Transcript.Scroll.ScrollToEnd();
        _hasUnseenText = false;
    }

    internal void UpdateUnreadIndicator()
    {
        if (Transcript.Scroll.AtEnd)
            _hasUnseenText = false;
        SetUnreadIndicatorState(_hasUnseenText);
    }

    private bool _flashStarted;

    private void SetUnreadIndicatorState(bool unread)
    {
        if (_unreadIndicator is null)
            return;

        if (!unread)
        {
            _unreadIndicator.Visible = false;
            _flashStarted = false;
            return;
        }

        if (!_flashStarted)
        {
            // Rising edge: start the authored sequence. Set ONCE — restarting
            // it every frame would hold it on frame zero and it would never
            // appear to blink at all.
            _flashStarted = true;
            _unreadIndicator.Visible = true;
            if (_unreadIndicator is IUiDatStateful starting)
                starting.TrySetRetailState(UiButtonStateMachine.Normal);
            return;
        }

        if (_unreadIndicator is UiButton flashing
            && string.Equals(flashing.ActiveState, "Ghosted", StringComparison.Ordinal))
        {
            _unreadIndicator.Visible = false;
        }

    }

    internal void StartTell(string name)
    {
        Input.SetText($"@tell {name}, ");
        FindRootOf(Input)?.SetKeyboardFocus(Input);
    }

    internal void EnterChatMode(KeyChord? physicalChord = null)
    {
        UiRoot? root = FindRootOf(Input);
        root?.SetKeyboardFocus(Input);
        if (physicalChord is { Device: 0 } chord)
            root?.SuppressPhysicalKeyUntilRelease(chord.Key);
        Input.SelectAllText();
    }

    internal void ToggleChatEntry(KeyChord? physicalChord = null)
    {
        UiRoot? root = FindRootOf(Input);
        if (root is null)
            return;
        root.SetKeyboardFocus(ReferenceEquals(root.KeyboardFocus, Input) ? null : Input);
        if (physicalChord is { Device: 0 } chord)
            root.SuppressPhysicalKeyUntilRelease(chord.Key);
    }

    internal void StartCommand()
    {
        Input.SetText("/");
        FindRootOf(Input)?.SetKeyboardFocus(Input);
    }

    internal void StartReply(string? name)
    {
        if (!string.IsNullOrEmpty(name))
            StartTell(name);
    }

    private static UiRoot? FindRootOf(UiElement element)
    {
        for (UiElement? at = element; at is not null; at = at.Parent)
            if (at is UiRoot root)
                return root;
        return null;
    }

    private IReadOnlyList<UiText.Line> StoreTranscriptLayout(
        IReadOnlyList<UiText.Line> lines,
        long revision,
        ulong filter,
        float wrapWidth,
        UiDatFont? datFont,
        BitmapFont? debugFont)
    {
        _cachedTranscriptRevision = revision;
        _cachedFilter = filter;
        _cachedTranscriptWrapWidth = wrapWidth;
        _cachedTranscriptDatFont = datFont;
        _cachedTranscriptDebugFont = debugFont;
        _cachedTranscriptLines = lines;
        TranscriptLayoutBuildCount++;
        return lines;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
