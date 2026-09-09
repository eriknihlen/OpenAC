using System.Globalization;
using System.Numerics;
using AcDream.Core.Chat;
using AcDream.Core.Combat;

namespace AcDream.UI.Abstractions.Panels.Chat;

public sealed class ChatVM : IDisposable, IChatCommandFeedback
{
    /// <summary>Default number of tail entries rendered.</summary>
    public const int DefaultDisplayLimit = 20;

    private readonly ChatLog _log;
    private readonly ChatCommandTargetState _commandTargets;
    private readonly bool _ownsCommandTargets;
    private readonly int _displayLimit;
    private bool _disposed;

    public string? LastIncomingTellSender =>
        _commandTargets.LastIncomingTellSender;

    public string? LastOutgoingTellTarget =>
        _commandTargets.LastOutgoingTellTarget;

    public string? LastMonarchSender =>
        _commandTargets.LastMonarchSender;

    public string? LastPatronSender =>
        _commandTargets.LastPatronSender;

    public Func<float>? FpsProvider { get; init; }

    public Func<Vector3>? PositionProvider { get; init; }

    public Action<string>? OnInterfaceText { get; init; }

    public long Revision => _log.Revision;

    public ChatVM(
        ChatLog log,
        int displayLimit = DefaultDisplayLimit,
        ChatCommandTargetState? commandTargets = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        if (displayLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(displayLimit), displayLimit, "must be >= 1");
        _displayLimit = displayLimit;
        _commandTargets = commandTargets ?? new ChatCommandTargetState(_log);
        _ownsCommandTargets = commandTargets is null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_ownsCommandTargets)
            _commandTargets.Dispose();
        _disposed = true;
    }

    public void ShowSystemMessage(string text) => _log.OnSystemMessage(text, chatType: 0x00u);

    public void ShowInterfaceText(string text)
    {
        if (OnInterfaceText is { } hook)
            hook(text);
        else
            _log.OnSystemMessage(text, chatType: (uint)RetailLogTextType.ClientLocal);
    }

    public void Clear() => _log.Clear();

    public void ResetSessionTargets()
    {
        _commandTargets.ResetSession();
    }

    public void ShowFps()
    {
        var fps = FpsProvider?.Invoke();
        ShowSystemMessage(fps is null
            ? "Framerate: (provider unavailable)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Framerate: {fps.Value:F1} FPS"));
    }

    public void ShowLocation()
    {
        var pos = PositionProvider?.Invoke();
        ShowSystemMessage(pos is null
            ? "Location: (provider unavailable)"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Location: ({pos.Value.X:F1}, {pos.Value.Y:F1}, {pos.Value.Z:F1})"));
    }

    public IReadOnlyList<string> RecentLines()
    {
        var snap = _log.Snapshot();
        int start = Math.Max(0, snap.Length - _displayLimit);
        int count = snap.Length - start;
        if (count <= 0) return Array.Empty<string>();

        bool timestamps = _log.DisplayTimestampsSource?.Invoke() == true;
        var lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            var entry = snap[start + i];
            lines[i] = timestamps
                ? ChatLog.FormatTimestampPrefix(entry.Received) + FormatEntry(entry)
                : FormatEntry(entry);
        }
        return lines;
    }

    public static string FormatEntry(ChatEntry entry)
        => FormatEntry(entry, static sender => sender);

    private const uint FirstPlayerObjectId = 0x50000001u;
    private const uint LastPlayerObjectId = 0x6FFFFFFFu;

    public static string FormatEntryTagged(ChatEntry entry)
        => ShouldTagSender(entry)
            ? FormatEntry(
                entry,
                sender =>
                    $"<Tell:IIDString:{entry.SenderGuid}:{sender}>{sender}<\\Tell>")
            : FormatEntry(entry);

    internal static bool ShouldTagSender(ChatEntry entry)
        => entry.SenderGuid >= FirstPlayerObjectId
            && entry.SenderGuid <= LastPlayerObjectId
            && !string.IsNullOrEmpty(entry.Sender)
            && entry.Sender.IndexOf('<') < 0
            && entry.Sender.IndexOf('>') < 0
            && !IsOwnSpeaker(entry.Sender)
            && entry.Kind is ChatKind.LocalSpeech
                or ChatKind.RangedSpeech
                or ChatKind.Channel
                or ChatKind.Tell;

    private static string FormatEntry(
        ChatEntry entry, Func<string, string> decorateSender) => entry.Kind switch
    {
        ChatKind.LocalSpeech   => IsOwnSpeaker(entry.Sender)
            ? $"You say, \"{entry.Text}\""
            : $"{decorateSender(entry.Sender)} says, \"{entry.Text}\"",
        ChatKind.RangedSpeech  => IsOwnSpeaker(entry.Sender)
            ? $"You shout, \"{entry.Text}\""
            : $"{decorateSender(entry.Sender)} shouts, \"{entry.Text}\"",
        ChatKind.Channel       => IsOwnSpeaker(entry.Sender)
            ? $"[{ChannelLabel(entry)}] You say, \"{entry.Text}\""
            : $"[{ChannelLabel(entry)}] {decorateSender(entry.Sender)} says, \"{entry.Text}\"",
        ChatKind.Tell          => entry.SenderGuid != 0
            ? $"{decorateSender(entry.Sender)} tells you, \"{entry.Text}\""
            : $"You tell {entry.Sender}, \"{entry.Text}\"",
        ChatKind.System        => entry.Text,
        ChatKind.Popup         => $"[Popup] {entry.Text}",
        ChatKind.Emote         => $"* {entry.Sender} {entry.Text}",
        ChatKind.SoulEmote     => $"* {entry.Sender} {entry.Text}",
        ChatKind.Combat        => entry.Text,
        _                      => entry.Text,
    };

    private static bool IsOwnSpeaker(string sender) =>
        string.IsNullOrEmpty(sender) || sender == "You";

    private static string ChannelLabel(ChatEntry entry) =>
        string.IsNullOrEmpty(entry.ChannelName)
            ? $"ch {entry.ChannelId}"
            : entry.ChannelName;

    public IReadOnlyList<FormattedLine> RecentLinesDetailed()
    {
        var snap = _log.Snapshot();
        int start = Math.Max(0, snap.Length - _displayLimit);
        int count = snap.Length - start;
        if (count <= 0) return Array.Empty<FormattedLine>();

        bool timestamps = _log.DisplayTimestampsSource?.Invoke() == true;
        var lines = new FormattedLine[count];
        for (int i = 0; i < count; i++)
        {
            var entry = snap[start + i];

            bool tagged = ShouldTagSender(entry);
            string markup = FormatEntryTagged(entry);
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

            lines[i] = new FormattedLine(
                Text: text,
                Kind: entry.Kind,
                CombatKind: entry.CombatKind,
                LogTextType: entry.LogTextType,
                Spans: spans);
        }
        return lines;
    }
}

public readonly record struct FormattedLine(
    string Text,
    ChatKind Kind,
    CombatLineKind? CombatKind,
    uint LogTextType,
    IReadOnlyList<ChatTextSpan>? Spans = null);
