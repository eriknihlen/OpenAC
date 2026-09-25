using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Gameplay;

/// <summary>
/// The names plugins use for the character's on/off options: the name each
/// setting has on the character options page. The chat-channel options are
/// called "Hear...Chat" there; the client's own "ListenTo..." spelling of
/// them is accepted too. Names are compared without regard to case.
/// </summary>
public static class CharacterOptionNames
{
    private static readonly Dictionary<CharacterOptionId, string> PageNames = new()
    {
        [CharacterOptionId.ListenToAllegianceChat] = "HearAllegianceChat",
        [CharacterOptionId.ListenToGeneralChat] = "HearGeneralChat",
        [CharacterOptionId.ListenToTradeChat] = "HearTradeChat",
        [CharacterOptionId.ListenToLFGChat] = "HearLFGChat",
        [CharacterOptionId.ListenToRoleplayChat] = "HearRoleplayChat",
        [CharacterOptionId.ListenToSocietyChat] = "HearSocietyChat",
    };

    private static readonly Dictionary<string, CharacterOptionId> ByName = Build();

    /// <summary>Every option the client keeps, by its page name, in option order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [.. CharacterOptionTable.All.Select(static entry => NameOf(entry.Id))];

    /// <summary>The page name of one option.</summary>
    public static string NameOf(CharacterOptionId id) =>
        PageNames.TryGetValue(id, out string? name) ? name : id.ToString();

    /// <summary>
    /// The option a name stands for. False for a name no option has,
    /// including a bare number.
    /// </summary>
    public static bool TryResolve(string? name, out CharacterOptionId id)
    {
        if (name is not null && ByName.TryGetValue(name.Trim(), out id))
            return true;
        id = default;
        return false;
    }

    private static Dictionary<string, CharacterOptionId> Build()
    {
        var names = new Dictionary<string, CharacterOptionId>(
            StringComparer.OrdinalIgnoreCase);
        foreach (CharacterOptionTableEntry entry in CharacterOptionTable.All)
        {
            names[entry.Id.ToString()] = entry.Id;
            names[NameOf(entry.Id)] = entry.Id;
        }
        return names;
    }
}
