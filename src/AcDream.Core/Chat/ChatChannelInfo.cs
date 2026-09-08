namespace AcDream.Core.Chat;

public enum ChatChannelSource
{
    Legacy,

    Turbine,
}

public abstract record ChatChannelInfo(string DisplayName, ChatChannelSource Source)
{
    public sealed record Legacy(uint ChannelId, string DisplayName)
        : ChatChannelInfo(DisplayName, ChatChannelSource.Legacy)
    {
        public override bool IsSelfEchoChannel()
        {
            return ChannelId switch
            {
                0x00000800u => true,  // Fellow
                0x00001000u => true,  // Vassals
                0x00002000u => true,  // Patron
                0x00004000u => true,  // Monarch
                0x01000000u => true,
                0x02000000u => true,  // AllegianceBroadcast
                _ => false,
            };
        }
    }

    public sealed record Turbine(
        uint RoomId,
        uint ChatType,
        uint DispatchType,
        string DisplayName)
        : ChatChannelInfo(DisplayName, ChatChannelSource.Turbine)
    {
        public override bool IsSelfEchoChannel()
        {
            return false;
        }
    }

    public abstract bool IsSelfEchoChannel();
}
