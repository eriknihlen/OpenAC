namespace AcDream.Runtime.Chat;

public enum ClientCommandId
{
    LifestoneRecall,

    MarketplaceRecall,
    PkArenaRecall,
    PkLiteArenaRecall,
    EnterPkLite,
    HouseRecall,
    MansionRecall,
    QueryAge,
    QueryBirth,
    ToggleFrameRate,
    TogglePersistentDaylight,
    RenderOption,
    ToggleUiLock,
    ShowVersion,
    ShowLocation,
    ShowLastCorpseLocation,
    Die,
    ClearChat,

    ChatLogFile,
    SaveUi,
    LoadUi,
    SaveAutoUi,
    LoadAutoUi,
    Away,
    Consent,
    Emote,
    ListEmotes,
    Friends,
    FriendsAdd,
    FriendsRemove,
    Squelch,
    Unsquelch,
    Filter,
    Unfilter,
    ListMessageTypes,
    FillComponents,

    Endurance,
    /// <summary>@speaker — fixed deprecation notice ("see @allegiance officer").</summary>
    Speaker,
    SetChatTitle,
    ChatToggle,
    /// <summary>@notell on|off — global Tell squelch toggle (message type 3).</summary>
    NoTellToggle,
    JoinChannel,
    LeaveChannel,
    Permit,
    /// <summary>@hslist &lt;type&gt; / "@house available" — list houses available for purchase.</summary>
    HouseAvailableList,
    IndexChannels,
    ListChannel,
    OnChannel,
    OffChannel,
    /// <summary>@alh / @ah / "@allegiance hometown" / "@allegiance ho" — recall to the allegiance bindstone.</summary>
    AllegianceHometown,
    /// <summary>"@allegiance info &lt;name&gt;" — request allegiance member info.</summary>
    AllegianceInfo,
    /// <summary>"@allegiance boot [-account] &lt;name&gt;".</summary>
    AllegianceBoot,
    /// <summary>"@allegiance ban ..." administration dispatcher.</summary>
    AllegianceBan,
    AllegianceChat,
    /// <summary>"@allegiance broadcast &lt;text&gt;".</summary>
    AllegianceBroadcast,
    /// <summary>"@allegiance officer ..." administration dispatcher.</summary>
    AllegianceOfficer,
    /// <summary>"@allegiance title ..." officer-title dispatcher.</summary>
    AllegianceOfficerTitle,
    /// <summary>"@allegiance name ..." dispatcher.</summary>
    AllegianceName,
    /// <summary>"@allegiance lock ..." dispatcher.</summary>
    AllegianceLock,
    /// <summary>"@allegiance house ..." allegiance-house dispatcher.</summary>
    AllegianceHouse,
    /// <summary>"@allegiance motd ..." and standalone "@motd ...".</summary>
    AllegianceMotd,
    HouseAbandon,
    HouseOpenStatus,
    /// <summary>"@house storage ...".</summary>
    HouseStorage,
    /// <summary>"@house remove|boot ...".</summary>
    HouseBoot,
    /// <summary>"@house boot_all|remove_all".</summary>
    HouseBootAll,
    /// <summary>"@house guest ...".</summary>
    HouseGuests,
    /// <summary>"@house hooks on|off".</summary>
    HouseHooks,
    AllegianceUnrecognizedSubcommand,

    HouseUnrecognizedSubcommand,
}
