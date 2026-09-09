using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal sealed class BuffCastability : IBuffCastability
{
    private readonly ISpellCatalog _catalog;
    private readonly IMagicCommands _magic;
    private readonly IReadOnlyList<PluginInventoryItem> _owned;
    private readonly string _blacklistedComponents;
    private readonly Action<string> _warn;
    private readonly Action<BuffLine, PluginSpellInfo?, IReadOnlyList<BuffTierRejection>>
        _noteTierPick;

    private readonly Dictionary<string, int> _counts =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<uint, bool> _hostComponents = [];

    public BuffCastability(
        ISpellCatalog catalog,
        IMagicCommands magic,
        IReadOnlyList<PluginInventoryItem> owned,
        string blacklistedComponents,
        Action<string> warn,
        Action<BuffLine, PluginSpellInfo?, IReadOnlyList<BuffTierRejection>> noteTierPick)
    {
        _catalog = catalog;
        _magic = magic;
        _owned = owned;
        _blacklistedComponents = blacklistedComponents;
        _warn = warn;
        _noteTierPick = noteTierPick;
    }

    public bool IsCastable(PluginSpellInfo tier) => IsCastable(tier, out _);

    public bool IsCastable(PluginSpellInfo tier, out string? reason)
    {
        if (SpellComponentPolicy.UsesBlacklistedComponent(
                _catalog, tier, _blacklistedComponents))
        {
            reason = "blacklisted";
            return false;
        }
        if (!HasScarabsInInventory(tier, out string? missingScarab))
        {
            reason = missingScarab;
            return false;
        }
        if (!HasHostComponents(tier.SpellId))
        {
            reason = "no components";
            return false;
        }
        reason = null;
        return true;
    }

    /// <inheritdoc/>
    public void NoteTierPick(
        BuffLine line, PluginSpellInfo? pick, IReadOnlyList<BuffTierRejection> rejections) =>
        _noteTierPick(line, pick, rejections);

    private bool HasHostComponents(uint spellId)
    {
        if (_hostComponents.TryGetValue(spellId, out bool cached))
            return cached;
        bool answer = _magic.HasComponents(spellId);
        _hostComponents[spellId] = answer;
        return answer;
    }

    public void NoteNoCastableTier(BuffLine line)
    {
        if (line.Tiers.Count == 0)
            return;
        _warn("No spell known for class including: "
            + line.Tiers[0].Name + ", buff SKIPPED.");
    }

    private bool HasScarabsInInventory(in PluginSpellInfo spell, out string? missingReason)
    {
        missingReason = null;
        if (spell.FormulaComponentIds.Count == 0)
            return true;

        Dictionary<string, int>? needed = null;
        foreach (uint componentId in spell.FormulaComponentIds)
        {
            if (!_catalog.TryGetComponent(
                    componentId, out PluginSpellComponentInfo component))
            {
                continue;
            }
            if (component.Name.Length == 0
                || !component.Name.Contains("Scarab", StringComparison.Ordinal))
            {
                continue;
            }
            needed ??= new Dictionary<string, int>(StringComparer.Ordinal);
            needed[component.Name] =
                needed.TryGetValue(component.Name, out int already)
                    ? already + 1
                    : 1;
        }
        if (needed is null)
            return true;

        foreach (KeyValuePair<string, int> pair in needed)
        {
            if (CountOwned(pair.Key) >= pair.Value)
                continue;
            _warn("Warning: You do not have enough of the item \"" + pair.Key
                + "\". Spells using it have been disabled.");
            missingReason = "no " + pair.Key;
            return false;
        }
        return true;
    }

    /// <summary>
    /// <c>g6.b(string)</c> (<c>g6.cs:236-247</c>) — every owned item of that
    /// name, summing <c>bc.cv</c> (StackCount, defaulting to 1).
    /// </summary>
    private int CountOwned(string name)
    {
        if (_counts.TryGetValue(name, out int cached))
            return cached;
        int total = 0;
        foreach (PluginInventoryItem item in _owned)
        {
            if (!string.Equals(item.Name, name, StringComparison.Ordinal))
                continue;
            total += item.StackSize > 0 ? item.StackSize : 1;
        }
        _counts[name] = total;
        return total;
    }
}
