namespace AcDream.UI.Abstractions.Panels.Settings;

public sealed record ChatSettings(
    bool HearGeneralChat,
    bool HearTradeChat,
    bool HearLFGChat,
    bool HearRoleplayChat,
    bool HearSocietyChat,
    bool AppearOffline,                  // 0x1000  — hide /who status
    bool ShowTimestamps,
    bool FilterProfanity,
    float FontSize,
    ulong ChatWindow1Filter = 0x0000101Cu,   // Speech, Tell, Speech_Direct_Send, Emote
    ulong ChatWindow2Filter = 0x00040C00u,   // Social, Social_Send, Allegiance
    ulong ChatWindow3Filter = 0x00080000u,   // Fellowship
    ulong ChatWindow4Filter = 0x78000000u,
    ulong ChatWindowMainFilter = 0xFBFFFFFFu,
    float DefaultOpacity = 1.0f,
    float ActiveOpacity = 1.0f,
    int ChatFontFace = 2,
    int ChatFontSizeIndex = 1)
{
    public static ChatSettings Default { get; } = new(
        HearGeneralChat:    true,
        HearTradeChat:      true,
        HearLFGChat:        true,
        HearRoleplayChat:   false,
        HearSocietyChat:    false,
        AppearOffline:      false,
        ShowTimestamps:     true,
        FilterProfanity:    true,
        FontSize:           12f);
}
