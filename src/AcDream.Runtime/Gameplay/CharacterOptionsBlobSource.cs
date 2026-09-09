using AcDream.Core.Items;

namespace AcDream.Runtime.Gameplay;

public readonly record struct CharacterOptionsBlobEcho(
    uint Options1,
    uint Options2,
    IReadOnlyList<ShortcutEntry> Shortcuts,
    IReadOnlyList<IReadOnlyList<uint>> FavoriteSpells,
    IReadOnlyDictionary<uint, uint> DesiredComponents,
    uint SpellbookFilters);

public static class CharacterOptionsBlobSource
{
    public static CharacterOptionsBlobEcho Capture(
        RuntimeCharacterState character,
        ShortcutStore shortcuts)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(shortcuts);

        var favorites = new IReadOnlyList<uint>[8];
        for (int tab = 0; tab < 8; tab++)
            favorites[tab] = character.Spellbook.GetFavorites(tab);

        return new CharacterOptionsBlobEcho(
            character.Options.Options1,
            character.Options.Options2,
            shortcuts.Items,
            favorites,
            character.Spellbook.DesiredComponents,
            character.Spellbook.SpellbookFilters);
    }
}
