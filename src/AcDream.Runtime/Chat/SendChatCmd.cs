namespace AcDream.Runtime.Chat;

public sealed record SendChatCmd(ChatChannelKind Channel, string? TargetName, string Text);
