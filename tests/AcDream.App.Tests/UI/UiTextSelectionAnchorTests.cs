using System;
using System.Collections.Generic;
using System.Numerics;
using AcDream.App.UI;
using AcDream.App.UI.Layout;
using AcDream.Core.Chat;
using AcDream.UI.Abstractions.Panels.Chat;

namespace AcDream.App.Tests.UI;

/// <summary>
/// #39: a text selection in the chat window must stay on the text it was made on while new
/// lines arrive. The transcript is rebuilt from scratch every frame and the messages shown
/// slide as new ones come in, so a selection held as raw line indices lands on different text
/// with every new line. Anchoring it to the message identity instead keeps it put; when the
/// message itself scrolls out of the log the selection goes with it.
/// </summary>
public class UiTextSelectionAnchorTests
{
    private static readonly Vector4 White = Vector4.One;

    private static IReadOnlyList<UiText.Line> Lines(params string[] texts)
    {
        var list = new List<UiText.Line>(texts.Length);
        foreach (string t in texts)
            list.Add(new UiText.Line(t, White));
        return list;
    }

    private static IReadOnlyList<UiText.LineKey> Keys(params (long Source, int Start)[] keys)
    {
        var list = new List<UiText.LineKey>(keys.Length);
        foreach ((long source, int start) in keys)
            list.Add(new UiText.LineKey(source, start));
        return list;
    }

    // ── TryResolveAnchor: the pure mapping from "this text" back to "this line" ──────────

    [Fact]
    public void TryResolveAnchor_FindsTheCharacterAfterEverythingShifted()
    {
        // Message 7 used to be the first line; two newer messages pushed it down two rows.
        var lines = Lines("newest", "newer", "hello world");
        var keys = Keys((9, 0), (8, 0), (7, 0));

        Assert.True(UiText.TryResolveAnchor(keys, lines, source: 7, offset: 6, out UiText.Pos pos));
        Assert.Equal(new UiText.Pos(2, 6), pos);
    }

    [Fact]
    public void TryResolveAnchor_AcceptsTheEndOfALine()
    {
        var lines = Lines("hello");
        var keys = Keys((1, 0));

        Assert.True(UiText.TryResolveAnchor(keys, lines, source: 1, offset: 5, out UiText.Pos pos));
        Assert.Equal(new UiText.Pos(0, 5), pos);
    }

    [Fact]
    public void TryResolveAnchor_FollowsTheCharacterAcrossAReWrap()
    {
        // One message, wrapped two ways. Character 8 of the message is the same letter either
        // way, so both wraps must resolve to it.
        var narrow = Lines("hello", "world!");     // "hello", then "world!" starting at offset 6
        var wide = Lines("hello world!");

        Assert.True(UiText.TryResolveAnchor(
            Keys((4, 0), (4, 6)), narrow, source: 4, offset: 8, out UiText.Pos narrowPos));
        Assert.Equal(new UiText.Pos(1, 2), narrowPos);
        Assert.Equal('r', narrow[narrowPos.Line].Text[narrowPos.Col]);

        Assert.True(UiText.TryResolveAnchor(
            Keys((4, 0)), wide, source: 4, offset: 8, out UiText.Pos widePos));
        Assert.Equal(new UiText.Pos(0, 8), widePos);
        Assert.Equal('r', wide[widePos.Line].Text[widePos.Col]);
    }

    [Fact]
    public void TryResolveAnchor_ReportsAMessageThatIsNoLongerShown()
    {
        Assert.False(UiText.TryResolveAnchor(
            Keys((9, 0), (8, 0)), Lines("newest", "newer"), source: 7, offset: 0, out _));
    }

    [Fact]
    public void TryResolveAnchor_ClampsPastTheEndOfAShortenedMessage()
    {
        // The wrap dropped the tail this offset pointed at: land on the end of what is shown
        // rather than on unrelated text.
        Assert.True(UiText.TryResolveAnchor(
            Keys((3, 0)), Lines("hi"), source: 3, offset: 40, out UiText.Pos pos));
        Assert.Equal(new UiText.Pos(0, 2), pos);
    }

    [Fact]
    public void TryResolveAnchor_ClampsBeforeTheStartOfATrimmedMessage()
    {
        // The oldest message had its head cut off by the transcript character budget.
        Assert.True(UiText.TryResolveAnchor(
            Keys((3, 20)), Lines("tail only"), source: 3, offset: 4, out UiText.Pos pos));
        Assert.Equal(new UiText.Pos(0, 0), pos);
    }

    // ── The element: a selection made once, seen through many rebuilds ──────────────────

    private sealed class Transcript
    {
        public readonly UiText Text = new() { Selectable = true };
        public List<UiText.Line> CurrentLines = new();
        public List<UiText.LineKey> CurrentKeys = new();

        public Transcript(bool keyed)
        {
            Text.LinesProvider = () => CurrentLines;
            if (keyed)
                Text.LineKeysProvider = () => CurrentKeys;
        }

        public void Show(IEnumerable<(long Source, string Text)> rows)
        {
            CurrentLines = new List<UiText.Line>();
            CurrentKeys = new List<UiText.LineKey>();
            foreach ((long source, string line) in rows)
            {
                CurrentLines.Add(new UiText.Line(line, White));
                CurrentKeys.Add(new UiText.LineKey(source, 0));
            }
            Text.RefreshLines();
        }

        /// <summary>
        /// Show rows whose last row carries no identity of its own — the two lists one step out
        /// of step, which must not cost the selection its anchor.
        /// </summary>
        public void ShowWithOneUnkeyedTrailingRow(IEnumerable<(long Source, string Text)> rows)
        {
            Show(rows);
            CurrentKeys.RemoveAt(CurrentKeys.Count - 1);
            Text.RefreshLines();
        }

        public void DragRows(int fromRow, int toRow) => Drag(Text, fromRow, toRow);
    }

    /// <summary>
    /// Drag from the top of one row to the top of another. Without a font every hit lands on
    /// column 0, which selects whole rows — enough to tell which text got selected.
    /// </summary>
    private static void Drag(UiText text, int fromRow, int toRow)
    {
        const int LineHeight = 16;
        text.OnEvent(new UiEvent(0, text, UiEventType.MouseDown, 0, 0, fromRow * LineHeight));
        text.OnEvent(new UiEvent(0, text, UiEventType.MouseMove, 0, 0, toRow * LineHeight));
        text.OnEvent(new UiEvent(0, text, UiEventType.MouseUp, 0, 0, toRow * LineHeight));
    }

    private static IEnumerable<(long, string)> Rows(params long[] sources)
    {
        foreach (long s in sources)
            yield return (s, "message " + s.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void KeyedSelection_SurvivesNewLinesPushingItUp()
    {
        var transcript = new Transcript(keyed: true);
        transcript.Show(Rows(3, 4, 5, 6));
        transcript.DragRows(1, 3);
        // What Ctrl+C copies is exactly what SelectedText assembles.
        Assert.Equal("message 4\nmessage 5\n", transcript.Text.SelectedText());

        // A new message arrives and the oldest falls off the top: the selected text is now a
        // row higher, rows 0..2 instead of 1..3.
        transcript.Show(Rows(4, 5, 6, 7));

        Assert.Equal("message 4\nmessage 5\n", transcript.Text.SelectedText());
    }

    [Fact]
    public void KeyedSelection_TracksTextAcrossAReWrap()
    {
        var transcript = new Transcript(keyed: true);
        transcript.Show(Rows(1, 2, 3));
        transcript.DragRows(1, 2);
        Assert.Equal("message 2\n", transcript.Text.SelectedText());

        // The window narrowed: message 2 now wraps onto two rows, so everything below moved.
        transcript.CurrentLines = new List<UiText.Line>
        {
            new("message 1", White),
            new("message", White),
            new(" 2", White),
            new("message 3", White),
        };
        transcript.CurrentKeys = new List<UiText.LineKey>
        {
            new(1, 0), new(2, 0), new(2, 7), new(3, 0),
        };
        transcript.Text.RefreshLines();

        Assert.Equal("message\n 2\n", transcript.Text.SelectedText());
    }

    [Fact]
    public void KeyedSelection_GoesAwayWithTheMessageItWasMadeOn()
    {
        var transcript = new Transcript(keyed: true);
        transcript.Show(Rows(3, 4, 5, 6));
        transcript.DragRows(0, 1);
        Assert.Equal("message 3\n", transcript.Text.SelectedText());

        // Message 3 scrolls out of the log entirely.
        transcript.Show(Rows(7, 8, 9, 10));

        Assert.Equal(string.Empty, transcript.Text.SelectedText());
    }

    [Fact]
    public void KeyedSelection_GoesAwayWhenTheTranscriptIsCleared()
    {
        var transcript = new Transcript(keyed: true);
        transcript.Show(Rows(1, 2, 3));
        transcript.DragRows(0, 2);
        Assert.NotEqual(string.Empty, transcript.Text.SelectedText());

        transcript.Show(Array.Empty<(long, string)>());

        Assert.Equal(string.Empty, transcript.Text.SelectedText());
    }

    [Fact]
    public void UnkeyedSelection_StillFollowsTheRowIndex()
    {
        // Text that never moves does not need identities, and must keep behaving exactly as it
        // did: the selection is a pair of line/column indices into whatever is shown now.
        var transcript = new Transcript(keyed: false);
        transcript.Show(Rows(3, 4, 5, 6));
        transcript.DragRows(1, 3);
        Assert.Equal("message 4\nmessage 5\n", transcript.Text.SelectedText());

        transcript.Show(Rows(4, 5, 6, 7));

        Assert.Equal("message 5\nmessage 6\n", transcript.Text.SelectedText());
    }

    [Fact]
    public void KeyedSelection_KeepsItsAnchorWhenARowHasNoIdentity()
    {
        var transcript = new Transcript(keyed: true);
        transcript.ShowWithOneUnkeyedTrailingRow(Rows(1, 2, 3, 4));

        // The drag ends on the row with no identity of its own. It takes the nearest one, so
        // the caret pins to message 3; dropping the anchor here would put the whole selection
        // back on raw row indices without a word about it.
        transcript.DragRows(1, 3);
        Assert.Equal("message 2\nmessage 3\n", transcript.Text.SelectedText());

        transcript.ShowWithOneUnkeyedTrailingRow(Rows(2, 3, 4, 5));

        // Both endpoints followed their own text one row up.
        Assert.Equal("message 2\n", transcript.Text.SelectedText());
        // Rows 1..3 of the new list would have been this, had the anchor been lost.
        Assert.NotEqual("message 3\nmessage 4\n", transcript.Text.SelectedText());
    }

    [Fact]
    public void BuildLines_WithNoCollectorsDoesNotScanForFragmentOffsets()
    {
        // Callers that want neither runs, tags, nor identities must pay for none of it.
        var detailed = new[]
        {
            new FormattedLine("one two", ChatKind.System, null, 0x00u, null, Sequence: 11),
        };

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW: 3f,
            measure: static s => s.Length,
            accept: null,
            defaultColor: White);

        Assert.Equal(["one", "two"], [.. System.Linq.Enumerable.Select(lines, l => l.Text)]);
    }

    // ── End to end over the real chat pipeline ──────────────────────────────────────────

    private sealed class ChatFixture
    {
        public readonly ChatLog Log;
        public readonly ChatVM Vm;
        public readonly UiText Text = new() { Selectable = true };
        private IReadOnlyList<UiText.Line> _lines = Array.Empty<UiText.Line>();
        private readonly List<UiText.LineKey> _keys = new();

        public ChatFixture(int maxEntries, int displayLimit)
        {
            Log = new ChatLog(maxEntries);
            Vm = new ChatVM(Log, displayLimit);
            Text.LinesProvider = () => _lines;
            Text.LineKeysProvider = () => _keys;
        }

        public void Say(string text)
            => Log.OnSelfSent(ChatKind.System, text, logTextType: 0x00u);

        public void Rebuild()
        {
            _lines = ChatTranscriptRenderer.BuildLines(
                Vm.RecentLinesDetailed(),
                maxW: 100_000f,
                measure: static s => s.Length,
                accept: null,
                defaultColor: White,
                keysPerLine: _keys);
            Text.RefreshLines();
        }
    }

    [Fact]
    public void ChatSelection_HoldsItsTextWhileTheLogKeepsFilling()
    {
        var chat = new ChatFixture(maxEntries: 4, displayLimit: 100);
        for (int i = 1; i <= 4; i++) chat.Say("line " + i);
        chat.Rebuild();

        Drag(chat.Text, 1, 3);
        Assert.Equal("line 2\nline 3\n", chat.Text.SelectedText());

        // One more line evicts the oldest entry: every surviving row index drops by one.
        chat.Say("line 5");
        chat.Rebuild();

        Assert.Equal("line 2\nline 3\n", chat.Text.SelectedText());
    }

    [Fact]
    public void ChatSelection_IsDroppedOnceItsLinesAreEvicted()
    {
        var chat = new ChatFixture(maxEntries: 4, displayLimit: 100);
        for (int i = 1; i <= 4; i++) chat.Say("line " + i);
        chat.Rebuild();

        Drag(chat.Text, 0, 1);
        Assert.Equal("line 1\n", chat.Text.SelectedText());

        chat.Say("line 5");
        chat.Rebuild();

        Assert.Equal(string.Empty, chat.Text.SelectedText());
    }

    [Fact]
    public void ChatLog_StampsEveryEntryWithAnIdentityThatIsNeverReused()
    {
        var log = new ChatLog(maxEntries: 2);
        log.OnSelfSent(ChatKind.System, "a", 0x00u);
        log.OnSelfSent(ChatKind.System, "b", 0x00u);
        log.OnSelfSent(ChatKind.System, "c", 0x00u);

        ChatEntry[] snapshot = log.Snapshot();
        Assert.Equal(2, snapshot.Length);
        Assert.Equal(2L, snapshot[0].Sequence);
        Assert.Equal(3L, snapshot[1].Sequence);

        log.Clear();
        log.OnSelfSent(ChatKind.System, "d", 0x00u);
        Assert.Equal(4L, log.Snapshot()[0].Sequence);
    }

    [Fact]
    public void BuildLines_KeysEveryWrappedRowToItsMessageAndOffset()
    {
        var detailed = new[]
        {
            new FormattedLine("one two", ChatKind.System, null, 0x00u, null, Sequence: 11),
            new FormattedLine("three", ChatKind.System, null, 0x00u, null, Sequence: 12),
        };
        var keys = new List<UiText.LineKey>();

        List<UiText.Line> lines = ChatTranscriptRenderer.BuildLines(
            detailed,
            maxW: 3f,
            measure: static s => s.Length,
            accept: null,
            defaultColor: White,
            keysPerLine: keys);

        Assert.Equal(lines.Count, keys.Count);
        Assert.Equal(new UiText.LineKey(11, 0), keys[0]);   // "one"
        Assert.Equal(new UiText.LineKey(11, 4), keys[1]);   // "two", past the dropped space
        Assert.Equal(new UiText.LineKey(12, 0), keys[2]);   // "three"
    }
}
