using AcDream.Core.Chat;
using AcDream.Runtime;

namespace AcDream.Headless.Hosting;

internal static class HeadlessConsoleChatFormatter
{
    internal static string? Format(in RuntimeChatEntry entry)
    {
        var kind = (ChatKind)entry.Kind;
        return kind switch
        {
            ChatKind.LocalSpeech or ChatKind.RangedSpeech =>
                $"[Local] {SpeakerLabel(entry.Sender)}: {entry.Text}",
            ChatKind.Channel =>
                $"[{ChannelLabel(entry)}] {SpeakerLabel(entry.Sender)}: {entry.Text}",
            ChatKind.Tell => FormatTell(entry),
            ChatKind.Emote or ChatKind.SoulEmote =>
                $"* {entry.Sender} {entry.Text}",
            ChatKind.Popup => $"[Popup] {entry.Text}",
            _ => entry.Text,
        };
    }

    private static string FormatTell(in RuntimeChatEntry entry) =>
        entry.SenderGuid != 0
            ? $"[Tell] {entry.Sender}: {entry.Text}"
            : $"[Tell] You -> {entry.Sender}: {entry.Text}";

    private static string SpeakerLabel(string sender) =>
        string.IsNullOrEmpty(sender) || sender == "You" ? "You" : sender;

    private static string ChannelLabel(in RuntimeChatEntry entry) =>
        string.IsNullOrEmpty(entry.ChannelName) ? "Channel" : entry.ChannelName;
}
