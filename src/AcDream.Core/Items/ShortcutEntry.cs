namespace AcDream.Core.Items;

public readonly record struct ShortcutEntry(int Index, uint ObjectId, uint SpellId)
{
    public ShortcutEntry WithIndex(int index) => this with { Index = index };
}
