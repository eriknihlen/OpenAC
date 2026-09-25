using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Chat;
using AcDream.Core.Combat;

namespace AcDream.Runtime.Chat;

/// <summary>
/// One chat-box line, already worded the way it is read on screen.
/// </summary>
/// <param name="Sequence">
/// Identity of the log entry this line was built from — stable while entries are appended
/// and older ones dropped. 0 when the line has no entry behind it.
/// </param>
/// <param name="LogTextType">
/// The text class the line belongs to; what a chat window's filter is keyed on.
/// </param>
/// <param name="Spans">
/// The line split at its clickable sender, or null when it has none. The spans
/// always rejoin to <paramref name="Text"/>.
/// </param>
public readonly record struct RuntimeChatLine(
    long Sequence,
    ChatKind Kind,
    uint LogTextType,
    CombatLineKind? CombatKind,
    DateTime Received,
    string Text,
    IReadOnlyList<ChatTextSpan>? Spans,
    string ChannelName);

/// <summary>
/// The chat box as a stream of finished lines: it words each entry, adds the
/// timestamp when that is switched on, and answers which chat window a line
/// belongs in. Every front end that shows chat reads it from here, so they all
/// show the same words; each front end keeps its own presentation (colour,
/// font, wrapping, scrollback) on top.
/// </summary>
public sealed class RuntimeChatFeed : IDisposable
{
    /// <summary>Default number of tail lines a snapshot returns.</summary>
    public const int DefaultLineLimit = 20;

    private readonly ChatLog _log;
    private readonly ChatWindowState? _windows;
    private bool _disposed;

    /// <param name="windows">
    /// The per-window filters. When null every line belongs to every window and
    /// the reader does its own filtering.
    /// </param>
    public RuntimeChatFeed(ChatLog log, ChatWindowState? windows = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _windows = windows;
        _log.EntryAppended += OnEntryAppended;
    }

    /// <summary>Raised as each new line is finished, in arrival order.</summary>
    public event Action<RuntimeChatLine>? LineAppended;

    /// <summary>
    /// Advances on every appended line. A reader that caches a laid-out
    /// transcript can hold on to it while this is unchanged.
    /// </summary>
    public long Revision => _log.Revision;

    public void Dispose()
    {
        if (_disposed)
            return;
        _log.EntryAppended -= OnEntryAppended;
        LineAppended = null;
        _disposed = true;
    }

    /// <summary>The last <paramref name="limit"/> lines, oldest first.</summary>
    public IReadOnlyList<RuntimeChatLine> Snapshot(int limit = DefaultLineLimit)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "must be >= 1");
        ChatEntry[] snap = _log.Snapshot();
        int start = Math.Max(0, snap.Length - limit);
        int count = snap.Length - start;
        if (count <= 0)
            return Array.Empty<RuntimeChatLine>();

        bool timestamps = _log.DisplayTimestampsSource?.Invoke() == true;
        var lines = new RuntimeChatLine[count];
        for (int i = 0; i < count; i++)
            lines[i] = BuildLine(snap[start + i], timestamps);
        return lines;
    }

    /// <summary>
    /// The last <paramref name="limit"/> lines that belong in the given chat
    /// window, oldest first.
    /// </summary>
    public IReadOnlyList<RuntimeChatLine> SnapshotForWindow(
        int windowId, int limit = DefaultLineLimit)
        => Snapshot(limit)
            .Where(line => BelongsTo(windowId, line.LogTextType))
            .ToArray();

    /// <summary>
    /// Whether a line of this text class is shown in the given chat window.
    /// Always true when the feed was built without per-window filters.
    /// </summary>
    public bool BelongsTo(int windowId, uint logTextType)
        => _windows is null
            || _windows.ShouldDisplay(
                windowId, ChatWindowState.BroadcastTargetWindow, logTextType);

    /// <summary>Words one entry the way it is read on screen.</summary>
    public RuntimeChatLine BuildLine(ChatEntry entry)
        => BuildLine(entry, _log.DisplayTimestampsSource?.Invoke() == true);

    private RuntimeChatLine BuildLine(ChatEntry entry, bool timestamps)
    {
        bool tagged = ShouldTagSender(entry);
        string markup = FormatTagged(entry);
        IReadOnlyList<ChatTextSpan>? spans = tagged
            ? ChatTagMarkup.Parse(markup)
            : null;
        string text = spans is null
            ? markup
            : string.Concat(spans.Select(span => span.Text));

        if (timestamps)
        {
            string prefix = ChatLog.FormatTimestampPrefix(entry.Received);
            spans = new[] { new ChatTextSpan(prefix, null, ChatSpanRole.Timestamp) }
                .Concat(spans ?? new[] { new ChatTextSpan(text, null) })
                .ToArray();
            text = prefix + text;
        }

        return new RuntimeChatLine(
            Sequence: entry.Sequence,
            Kind: entry.Kind,
            LogTextType: entry.LogTextType,
            CombatKind: entry.CombatKind,
            Received: entry.Received,
            Text: text,
            Spans: spans,
            ChannelName: entry.ChannelName);
    }

    private void OnEntryAppended(ChatEntry entry)
    {
        if (LineAppended is not { } handler)
            return;
        handler(BuildLine(entry));
    }

    // -- Wording -----------------------------------------------------------

    /// <summary>The line as plain text, with no clickable sender marked up.</summary>
    public static string Format(ChatEntry entry) => ChatLineWording.Format(entry);

    /// <summary>
    /// The line with another player's name wrapped so a front end that supports
    /// it can make the name clickable. Identical to <see cref="Format"/> for
    /// every line that does not name another player.
    /// </summary>
    public static string FormatTagged(ChatEntry entry) => ChatLineWording.FormatTagged(entry);

    /// <summary>Whether this line names another player we could click to tell.</summary>
    public static bool ShouldTagSender(ChatEntry entry) => ChatLineWording.ShouldTagSender(entry);
}
