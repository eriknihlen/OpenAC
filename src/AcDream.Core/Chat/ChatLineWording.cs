using System;

namespace AcDream.Core.Chat;

/// <summary>
/// How one chat entry reads on screen. The chat box, the transcript log, a
/// console and a plugin all take their wording from here, so a line reads the
/// same wherever it is shown.
/// </summary>
public static class ChatLineWording
{
    /// <summary>The line as plain text, with no clickable sender marked up.</summary>
    public static string Format(ChatEntry entry)
        => Format(entry, static sender => sender);

    /// <summary>
    /// The line with another player's name wrapped so a front end that supports
    /// it can make the name clickable. Identical to <see cref="Format(ChatEntry)"/>
    /// for every line that does not name another player.
    /// </summary>
    public static string FormatTagged(ChatEntry entry)
        => ShouldTagSender(entry)
            ? Format(
                entry,
                sender =>
                    $"<Tell:IIDString:{entry.SenderGuid}:{sender}>{sender}<\\Tell>")
            : Format(entry);

    /// <summary>Whether this line names another player we could click to tell.</summary>
    // A channel line names its speaker without an object id (allegiance,
    // fellowship and the other channels carry only the name), yet the name
    // is still a link that starts a tell. Every other kind links only a
    // speaker whose id is a player's.
    public static bool ShouldTagSender(ChatEntry entry)
        => (entry.Kind == ChatKind.Channel
                || PlayerObjectIds.IsPlayer(entry.SenderGuid))
            && !string.IsNullOrEmpty(entry.Sender)
            && entry.Sender.IndexOf('<') < 0
            && entry.Sender.IndexOf('>') < 0
            && !IsOwnSpeaker(entry.Sender)
            && !entry.IsTellToSelf
            && entry.Kind is ChatKind.LocalSpeech
                or ChatKind.RangedSpeech
                or ChatKind.Channel
                or ChatKind.Tell;

    private static string Format(
        ChatEntry entry, Func<string, string> decorateSender) => entry.Kind switch
    {
        ChatKind.LocalSpeech   => IsOwnSpeaker(entry.Sender)
            ? $"You say, \"{entry.Text}\""
            : $"{decorateSender(entry.Sender)} says, \"{entry.Text}\"",
        // A ranged line has no sentence of its own, not even for the
        // speaker: it is printed under the name it arrived with.
        ChatKind.RangedSpeech  => $"{decorateSender(entry.Sender)} says, \"{entry.Text}\"",
        ChatKind.Channel       => ChannelLine(entry, decorateSender),
        ChatKind.Tell when entry.IsTellToSelf => $"You think, \"{entry.Text}\"",
        ChatKind.Tell          => entry.SenderGuid != 0
            ? $"{decorateSender(entry.Sender)} tells you, \"{entry.Text}\""
            : $"You tell {entry.Sender}, \"{entry.Text}\"",
        ChatKind.System        => entry.Text,
        ChatKind.Popup         => $"[Popup] {entry.Text}",
        ChatKind.Emote         => EmoteLine(entry),
        ChatKind.SoulEmote     => EmoteLine(entry),
        ChatKind.Combat        => entry.Text,
        _                      => entry.Text,
    };

    /// <summary>
    /// An emote is the name followed by the action, with no marker in front.
    /// The space between them is left out when the action starts with an
    /// apostrophe, so "Bob" and "'s eyes narrow." read "Bob's eyes narrow."
    /// </summary>
    private static string EmoteLine(ChatEntry entry) =>
        entry.Text.StartsWith('\'')
            ? $"{entry.Sender}{entry.Text}"
            : $"{entry.Sender} {entry.Text}";

    private static bool IsOwnSpeaker(string sender) =>
        string.IsNullOrEmpty(sender) || sender == "You";

    /// <summary>
    /// A channel that arrives with a name is shown under that name. One that
    /// arrives as a bare number is one of the fixed channels, and each of
    /// those has a sentence of its own.
    /// </summary>
    private static string ChannelLine(
        ChatEntry entry, Func<string, string> decorateSender)
    {
        bool own = IsOwnSpeaker(entry.Sender);
        if (string.IsNullOrEmpty(entry.ChannelName))
        {
            return own
                ? LegacyChannelSentence.Sent(entry.ChannelId, entry.Text)
                : LegacyChannelSentence.Heard(
                    entry.ChannelId, decorateSender(entry.Sender), entry.Text);
        }
        return own
            ? $"[{entry.ChannelName}] You say, \"{entry.Text}\""
            : $"[{entry.ChannelName}] {decorateSender(entry.Sender)} says, \"{entry.Text}\"";
    }
}
