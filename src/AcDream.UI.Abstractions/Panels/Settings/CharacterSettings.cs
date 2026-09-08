namespace AcDream.UI.Abstractions.Panels.Settings;

public sealed record CharacterSettings(
    string DefaultChatChannel,            // "Local" / "Allegiance" / "Fellowship" / "General" / etc.
    bool   AutoAttack,
    bool   ConfirmSalvage,                // Prompt before salvaging valuable items
    bool   ShowPickupMessages)
{
    public static CharacterSettings Default { get; } = new(
        DefaultChatChannel: "Local",
        AutoAttack:         false,
        ConfirmSalvage:     true,
        ShowPickupMessages: true);

    public static System.Collections.Generic.IReadOnlyList<string> AvailableChannels { get; } = new[]
    {
        "Local",
        "Allegiance",
        "Fellowship",
        "General",
        "Trade",
        "LFG",
        "Roleplay",
    };
}
