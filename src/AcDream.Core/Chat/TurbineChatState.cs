namespace AcDream.Core.Chat;

public sealed class TurbineChatState
{
    /// <summary>True after the first <c>SetTurbineChatChannels</c> arrives.</summary>
    public bool Enabled { get; private set; }

    public uint AllegianceRoom { get; private set; }
    public uint GeneralRoom { get; private set; }
    public uint TradeRoom { get; private set; }
    public uint LfgRoom { get; private set; }
    public uint RoleplayRoom { get; private set; }
    public uint OlthoiRoom { get; private set; }
    /// <summary>Top-level Society room (0 if player has no society).</summary>
    public uint SocietyRoom { get; private set; }
    public uint SocietyCelestialHandRoom { get; private set; }
    public uint SocietyEldrytchWebRoom { get; private set; }
    public uint SocietyRadiantBloodRoom { get; private set; }

    private uint _nextContextId = 1;

    public uint NextContextId()
    {
        uint cookie = _nextContextId;
        unchecked
        {
            _nextContextId += 1;
            if (_nextContextId == 0) _nextContextId = 1;
        }
        return cookie;
    }

    public void OnChannelsReceived(
        uint allegianceRoom,
        uint generalRoom,
        uint tradeRoom,
        uint lfgRoom,
        uint roleplayRoom,
        uint olthoiRoom,
        uint societyRoom,
        uint societyCelestialHandRoom,
        uint societyEldrytchWebRoom,
        uint societyRadiantBloodRoom)
    {
        Enabled                  = true;
        AllegianceRoom           = allegianceRoom;
        GeneralRoom              = generalRoom;
        TradeRoom                = tradeRoom;
        LfgRoom                  = lfgRoom;
        RoleplayRoom             = roleplayRoom;
        OlthoiRoom               = olthoiRoom;
        SocietyRoom              = societyRoom;
        SocietyCelestialHandRoom = societyCelestialHandRoom;
        SocietyEldrytchWebRoom   = societyEldrytchWebRoom;
        SocietyRadiantBloodRoom  = societyRadiantBloodRoom;
    }

    public void Reset()
    {
        Enabled = false;
        AllegianceRoom = 0u;
        GeneralRoom = 0u;
        TradeRoom = 0u;
        LfgRoom = 0u;
        RoleplayRoom = 0u;
        OlthoiRoom = 0u;
        SocietyRoom = 0u;
        SocietyCelestialHandRoom = 0u;
        SocietyEldrytchWebRoom = 0u;
        SocietyRadiantBloodRoom = 0u;
        _nextContextId = 1u;
    }

    public uint RoomFor(ChatChannelKindLite kind) => kind switch
    {
        ChatChannelKindLite.Allegiance           => AllegianceRoom,
        ChatChannelKindLite.General              => GeneralRoom,
        ChatChannelKindLite.Trade                => TradeRoom,
        ChatChannelKindLite.Lfg                  => LfgRoom,
        ChatChannelKindLite.Roleplay             => RoleplayRoom,
        ChatChannelKindLite.Olthoi               => OlthoiRoom,
        ChatChannelKindLite.Society              => SocietyRoom,
        ChatChannelKindLite.SocietyCelestialHand => SocietyCelestialHandRoom,
        ChatChannelKindLite.SocietyEldrytchWeb   => SocietyEldrytchWebRoom,
        ChatChannelKindLite.SocietyRadiantBlood  => SocietyRadiantBloodRoom,
        _ => 0u,
    };
}

public enum ChatChannelKindLite
{
    Allegiance,
    General,
    Trade,
    Lfg,
    Roleplay,
    Society,
    SocietyCelestialHand,
    SocietyEldrytchWeb,
    SocietyRadiantBlood,
    Olthoi,
}
