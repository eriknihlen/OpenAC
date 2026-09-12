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
        bool isOwnEcho = _localPlayerGuid != 0 && senderGuid == _localPlayerGuid;
        string effectiveSender = (isOwnEcho || string.IsNullOrEmpty(sender)) ? "You" : sender;
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
        Append(new ChatEntry(
            Kind: ChatKind.Channel,
            Sender: sender,
            Text: text,
            SenderGuid: 0,
            ChannelId: channelId)
        {
            ChannelName = channelName,
            LogTextType = logTextType ?? LegacyChannelChatType.Resolve(channelId, ownSend: false),
        });
    }

    public void OnTellReceived(string sender, string text, uint senderGuid, uint logTextType)
    {
        Append(new ChatEntry(
            Kind: ChatKind.Tell,
            Sender: sender,
            Text: text,
            SenderGuid: senderGuid,
            ChannelId: 0)
        {
            LogTextType = logTextType,
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

    public void OnSelfSent(ChatKind kind, string text, uint logTextType, string targetOrChannel = "")
    {
        Append(new ChatEntry(
            Kind: kind,
            Sender: kind == ChatKind.Tell ? targetOrChannel : "",
            Text: text,
            SenderGuid: 0,
            ChannelId: 0)
        {
            ChannelName = kind == ChatKind.Channel ? targetOrChannel : "",
            LogTextType = logTextType,
        });
    }

    private void Append(ChatEntry entry)
    {
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
    /// Append order, unique for the lifetime of the log and never reused. 0 on an entry that
    /// was never appended; the log assigns it as the entry goes in.
    /// </summary>
    public long Sequence { get; init; }
}
