namespace AcDream.Runtime.Chat;

/// <summary>
/// A chat line on its way out. <c>TargetGuid</c> is non-zero only
/// when the addressee was picked in the world rather than typed by name; the
/// route then aims the line at that object instead of looking the name up.
/// </summary>
public sealed record SendChatCmd(
    ChatChannelKind Channel,
    string? TargetName,
    string Text,
    uint TargetGuid = 0u);
