using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Threading;

namespace AcDream.Core.Chat;

public sealed class ChatLog
{
    private readonly ConcurrentQueue<ChatEntry> _buffer = new();
    private readonly int _maxEntries;
    private uint _localPlayerGuid;
    private long _revision;
    private long _sequence;

    private string _lastSystemText = "";
    private DateTime _lastSystemAt = DateTime.MinValue;
    private static readonly TimeSpan SystemDedupWindow = TimeSpan.FromSeconds(1);

    public ChatLog(int maxEntries = 500)
    {
        if (maxEntries < 1) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _maxEntries = maxEntries;
    }

    public Func<bool>? DisplayTimestampsSource { get; set; }

    /// <summary>Whether the language filter is on; checked once per line appended.</summary>
    public Func<bool>? FilterLanguageSource { get; set; }

    /// <summary>The banned-word patterns to censor against; null when the taboo
    /// table failed to load, empty when it loaded with none.</summary>
    public IReadOnlyList<string>? FilterLanguagePatterns { get; set; }

    /// <summary>Fires every time a new entry is appended.</summary>
    public event Action<ChatEntry>? EntryAppended;

    public ChatEntry[] Snapshot() => _buffer.ToArray();

    public int Count => _buffer.Count;

    public long Revision => Interlocked.Read(ref _revision);

    public void SetLocalPlayerGuid(uint guid) => _localPlayerGuid = guid;

    public void ResetSessionIdentity()
    {
        _localPlayerGuid = 0u;
        _lastSystemText = string.Empty;
        _lastSystemAt = DateTime.MinValue;
    }

    // ── Inbound adapters ─────────────────────────────────────────────────────

    public void OnLocalSpeech(string sender, string text, uint senderGuid, bool isRanged, uint logTextType)
    {
        // Only a line said within earshot has a sentence of its own for the
        // speaker ("You say"). A ranged line is printed under the name it
        // arrived with, the speaker's own included; see the research note on
        // chat wording.
        bool isOwnEcho = _localPlayerGuid != 0 && senderGuid == _localPlayerGuid;
        string effectiveSender = !isRanged && (isOwnEcho || string.IsNullOrEmpty(sender))
            ? "You"
            : sender;
        Append(new ChatEntry(
            Kind: isRanged ? ChatKind.RangedSpeech : ChatKind.LocalSpeech,
            Sender: effectiveSender,
            Text: text,
            SenderGuid: senderGuid,
            ChannelId: 0)
        {
            LogTextType = logTextType,
        });
    }

    /// <summary>EmoteText (0x01E0) — server-driven third-person emote.</summary>
    public void OnEmote(string senderName, string text, uint senderGuid)
    {
        Append(new ChatEntry(
            Kind: ChatKind.Emote,
            Sender: senderName,
            Text: text,
            SenderGuid: senderGuid,
            ChannelId: 0)
        {
            LogTextType = (uint)RetailLogTextType.Emote,
        });
    }

    public void OnSoulEmote(string senderName, string text, uint senderGuid)
    {
        Append(new ChatEntry(
            Kind: ChatKind.SoulEmote,
            Sender: senderName,
            Text: text,
            SenderGuid: senderGuid,
            ChannelId: 0)
        {
            LogTextType = (uint)RetailLogTextType.Emote,
        });
    }

    public void OnPlayerKilled(
        string deathMessage,
        uint victimGuid,
        uint killerGuid,
        uint localPlayerGuid = 0u)
    {
        if (localPlayerGuid != 0u
            && (localPlayerGuid == victimGuid || localPlayerGuid == killerGuid))
        {
            return;
        }

        Append(new ChatEntry(
            Kind: ChatKind.System,
            Sender: "",
            Text: deathMessage,
            SenderGuid: victimGuid,
            ChannelId: killerGuid)
        {
            LogTextType = 0x00u,
        });
    }


    public void OnChannelBroadcast(
        uint channelId, string sender, string text, uint? logTextType = null, string channelName = "")
    {
        // A numbered channel's line with no speaker is the sender's own line
        // handed back, and takes the sender's text class; see the research
        // note on chat wording.
        bool ownLine = string.IsNullOrEmpty(sender);
        Append(new ChatEntry(
            Kind: ChatKind.Channel,
            Sender: sender,
            Text: text,
            SenderGuid: 0,
            ChannelId: channelId)
        {
            ChannelName = channelName,
            LogTextType = logTextType ?? LegacyChannelChatType.Resolve(channelId, ownSend: ownLine),
        });
    }

    /// <param name="targetGuid">
    /// Who the tell was addressed to. A tell whose speaker is its listener is
    /// one the character sent itself, and reads as a thought.
    /// </param>
    public void OnTellReceived(
        string sender, string text, uint senderGuid, uint logTextType, uint targetGuid = 0u)
    {
        Append(new ChatEntry(
            Kind: ChatKind.Tell,
            Sender: sender,
            Text: text,
            SenderGuid: senderGuid,
            ChannelId: 0)
        {
            LogTextType = logTextType,
            TargetGuid = targetGuid,
        });
    }

    public void OnSystemMessage(string text, uint chatType)
    {
        var now = DateTime.UtcNow;
        if (text == _lastSystemText && (now - _lastSystemAt) < SystemDedupWindow)
        {
            // Suppress the dup — the wire-level duplicate isn't a
            // user-meaningful signal. Reset the timer so a long burst
            // of the same text still skips.
            _lastSystemAt = now;
            return;
        }
        _lastSystemText = text;
        _lastSystemAt = now;

        Append(new ChatEntry(
            Kind: ChatKind.System,
            Sender: "",
            Text: text,
            SenderGuid: 0,
            ChannelId: chatType)
        {
            LogTextType = chatType,
        });
    }

    public void OnPopup(string text)
    {
        Append(new ChatEntry(
            Kind: ChatKind.Popup,
            Sender: "",
            Text: text,
            SenderGuid: 0,
            ChannelId: 0)
        {
            LogTextType = 0x00u,
        });
    }

    public void OnCombatLine(
        string text, uint logTextType, Combat.CombatLineKind kind = Combat.CombatLineKind.Info)
    {
        Append(new ChatEntry(
            Kind: ChatKind.Combat,
            Sender: "",
            Text: text,
            SenderGuid: 0,
            ChannelId: 0)
        {
            CombatKind = kind,
            LogTextType = logTextType,
        });
    }

    /// <param name="channelId">
    /// The numbered channel the text went to, when it went to one. Such a
    /// line is worded by its channel, so it is recorded by number and not by
    /// a display name.
    /// </param>
    public void OnSelfSent(
        ChatKind kind,
        string text,
        uint logTextType,
        string targetOrChannel = "",
        uint channelId = 0u)
    {
        Append(new ChatEntry(
            Kind: kind,
            Sender: kind == ChatKind.Tell ? targetOrChannel : "",
            Text: text,
            SenderGuid: 0,
            ChannelId: channelId)
        {
            ChannelName = kind == ChatKind.Channel ? targetOrChannel : "",
            LogTextType = logTextType,
        });
    }

    /// <summary>
    /// Predicates that can drop a line before it is appended. Because the drop
    /// happens here, a rejected line reaches nothing downstream of the log.
    /// </summary>
    public ChatSuppressionFilters Filters { get; } = new();

    /// <summary>
    /// Projects an entry into the shape filters are written against. The
    /// sequence is zero: the entry has not been appended yet, so it has none.
    /// </summary>
    public static Plugin.Abstractions.PluginChatMessage ToFilterCandidate(
        in ChatEntry entry) =>
        new(
            0UL,
            entry.SenderGuid,
            (int)entry.Kind,
            entry.Sender,
            entry.Text,
            entry.ChannelName)
        {
            LogTextType = unchecked((int)entry.LogTextType),
            CombatKind = entry.CombatKind is { } combat ? (int)combat + 1 : 0,
            Received = new DateTimeOffset(
                DateTime.SpecifyKind(entry.Received, DateTimeKind.Utc)),
            // The entry's number field carries other things on other kinds
            // (a system line's text class, a death line's killer); only a
            // channel line's number is a channel.
            ChannelId = entry.Kind == ChatKind.Channel ? entry.ChannelId : 0u,
            DisplayText = ChatLineWording.FormatTagged(entry),
        };

    private void Append(ChatEntry entry)
    {
        // The filters are offered the line as it arrived and may eat it; only
        // then is it censored and kept. That is the order the original client
        // uses with its own plugin host, so a filter matches the real words
        // while every reader gets the line as printed.
        if (Filters.ShouldSuppress(ToFilterCandidate(in entry)))
            return;
        if (FilterLanguageSource?.Invoke() == true)
            entry = entry with { Text = ChatLanguageFilter.Censor(entry.Text, FilterLanguagePatterns) };

        // Stamp every entry with an identity that is never reused, so anything holding on to
        // one line (a text selection, say) can still find it after older entries are dropped
        // and every remaining entry's position in the buffer has shifted.
        entry = entry with { Sequence = Interlocked.Increment(ref _sequence) };
        _buffer.Enqueue(entry);
        while (_buffer.Count > _maxEntries)
            _buffer.TryDequeue(out _);
        Interlocked.Increment(ref _revision);
        EntryAppended?.Invoke(entry);
    }

    public static string FormatTimestampPrefix(DateTime receivedUtc) =>
        receivedUtc.ToLocalTime().ToString(
            @"H\:mm\:ss ", CultureInfo.InvariantCulture);

    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) { /* drain */ }
        Interlocked.Increment(ref _revision);
    }
}

public enum ChatKind
{
    LocalSpeech,
    RangedSpeech,
    Channel,
    Tell,
    System,
    Popup,
    Emote,
    SoulEmote,
    Combat,
}

public readonly record struct ChatEntry(
    ChatKind Kind,
    string Sender,
    string Text,
    uint SenderGuid,
    uint ChannelId)
{
    public DateTime Received { get; init; } = DateTime.UtcNow;

    public Combat.CombatLineKind? CombatKind { get; init; }

    public string ChannelName { get; init; } = "";

    public uint LogTextType { get; init; } = 0x00u;

    /// <summary>
    /// Who a received tell was addressed to; 0 on every other line, and when
    /// the listener is not known.
    /// </summary>
    public uint TargetGuid { get; init; }

    /// <summary>A tell the character sent itself: its speaker is its listener.</summary>
    public bool IsTellToSelf =>
        Kind == ChatKind.Tell && SenderGuid != 0u && SenderGuid == TargetGuid;

    /// <summary>
    /// Append order, unique for the lifetime of the log and never reused. 0 on an entry that
    /// was never appended; the log assigns it as the entry goes in.
    /// </summary>
    public long Sequence { get; init; }
}
