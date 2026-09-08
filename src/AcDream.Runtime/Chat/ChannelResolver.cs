namespace AcDream.Runtime.Chat;

public static class ChannelResolver
{
    public readonly record struct Resolved(uint ChannelId, string DisplayName);

    public static Resolved? Resolve(ChatChannelKind kind) => kind switch
    {
        ChatChannelKind.Fellowship => new Resolved(0x00000800u, "Fellowship"),
        ChatChannelKind.AllegianceBroadcast => new Resolved(0x02000000u, "Allegiance"),
        ChatChannelKind.Vassals    => new Resolved(0x00001000u, "Vassals"),
        ChatChannelKind.Patron     => new Resolved(0x00002000u, "Patron"),
        ChatChannelKind.Monarch    => new Resolved(0x00004000u, "Monarch"),
        ChatChannelKind.CoVassals  => new Resolved(0x01000000u, "CoVassals"),
        _ => null,
    };
}
