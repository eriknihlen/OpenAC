namespace AcDream.Core.Chat;

public static class LegacyChannelChatType
{
    public static uint Resolve(uint channelId, bool ownSend) => channelId switch
    {
        0x0001u => (uint)RetailLogTextType.Abuse,

        0x0400u => (uint)RetailLogTextType.Help,

        // Fellowship: same type ("[Fellowship] ...") for hear and send.
        // pc:00570e48 (hear, m_buffer_5=0x13) / pc:00570d08 (send, 0x13).
        0x0800u => (uint)RetailLogTextType.Fellowship,

        // Patron / Vassal / Follower(Monarch): hear = Social (0xA,
        // "Your patron/vassal/follower X tells you..."); OWN send = Social_
        // Send (0xB, "You say to your patron/vassal/follower...").
        // pc:00570e50/00570e58 (Patron/Vassal hear, 0xa) + pc:00570e40
        // (Follower hear, 0xa); pc:00570c07/00570c21 (all three own-send
        // via the shared label_570c21, 0xb).
        0x1000u or 0x2000u or 0x4000u =>
            ownSend ? (uint)RetailLogTextType.SocialSend : (uint)RetailLogTextType.Social,

        // Co-Vassals / Allegiance Broadcast: same type for hear and send.
        // pc:00571025/00571014 (hear, 0xa) / pc:00570e17/00570df7 (send, 0xa).
        0x1000000u or 0x2000000u => (uint)RetailLogTextType.Social,

        0x4000000u =>
            ownSend ? (uint)RetailLogTextType.Fellowship : (uint)RetailLogTextType.Channel,

        _ => ownSend ? (uint)RetailLogTextType.ChannelSend : (uint)RetailLogTextType.Channel,
    };
}
