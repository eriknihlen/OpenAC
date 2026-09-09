using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum VtankCombatSpellType
{
    Vuln,
    War,
    Arc,
    Ring,
    Streak,
}

internal readonly record struct AttackSpellChoice(
    PluginSpellInfo Spell,
    VtankCombatSpellType Type,
    MonsterDamageType DamageType,
    bool CastWithoutTarget);

internal sealed class AttackSpellCatalog
{
    /// <summary>
    /// <c>fk.cs:560</c> — the Void ring is the ONLY table entry named by spell
    /// id rather than by name (<c>this.m_b.f.c(5361)</c>).
    /// </summary>
    public const uint VoidRingSpellId = 5361u;

    internal const uint TuskerFistsSpellId = 0x0B76u;
    private const uint WarMagicSkill = 34u;
    private const uint LifeMagicSkill = 33u;
    private const uint VoidMagicSkill = 43u;

    private readonly Dictionary<string, List<PluginSpellInfo>> _families;
    private readonly Dictionary<uint, PluginSpellInfo> _byId;

    private AttackSpellCatalog(
        Dictionary<string, List<PluginSpellInfo>> families,
        Dictionary<uint, PluginSpellInfo> byId)
    {
        _families = families;
        _byId = byId;
    }

    public static AttackSpellCatalog Build(IReadOnlyList<PluginSpellInfo> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var families = new Dictionary<string, List<PluginSpellInfo>>(
            StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<uint, PluginSpellInfo>();
        foreach (PluginSpellInfo spell in spells)
        {
            byId[spell.SpellId] = spell;
            string family = BaseName(spell.Name);
            if (family.Length == 0)
                continue;
            if (!families.TryGetValue(family, out List<PluginSpellInfo>? members))
                families[family] = members = [];
            members.Add(spell);
        }
        return new AttackSpellCatalog(families, byId);
    }

    public static string? FamilyName(
        MonsterDamageType element,
        VtankCombatSpellType type) => type switch
        {
            VtankCombatSpellType.Vuln => element switch
            {
                MonsterDamageType.Acid => "Acid Vulnerability Other",
                MonsterDamageType.Bludgeon => "Bludgeoning Vulnerability Other",
                MonsterDamageType.Cold => "Cold Vulnerability Other",
                MonsterDamageType.Fire => "Fire Vulnerability Other",
                MonsterDamageType.Electric => "Lightning Vulnerability Other",
                MonsterDamageType.Pierce => "Piercing Vulnerability Other",
                MonsterDamageType.Slash => "Blade Vulnerability Other",
                MonsterDamageType.Harm => "Drain Health Other",
                MonsterDamageType.Nether or MonsterDamageType.VoidBasic =>
                    "Destructive Curse",
                _ => null,
            },
            VtankCombatSpellType.War => element switch
            {
                MonsterDamageType.Acid => "Acid Stream",
                MonsterDamageType.Bludgeon => "Shock Wave",
                MonsterDamageType.Cold => "Frost Bolt",
                MonsterDamageType.Fire => "Flame Bolt",
                MonsterDamageType.Electric => "Lightning Bolt",
                MonsterDamageType.Pierce => "Force Bolt",
                MonsterDamageType.Slash => "Whirling Blade",
                MonsterDamageType.Harm => "Martyr's Hecatomb",
                MonsterDamageType.Nether or MonsterDamageType.VoidBasic =>
                    "Nether Bolt",
                _ => null,
            },
            VtankCombatSpellType.Arc => element switch
            {
                MonsterDamageType.Acid => "Acid Arc",
                MonsterDamageType.Bludgeon => "Shock Arc",
                MonsterDamageType.Cold => "Frost Arc",
                MonsterDamageType.Fire => "Flame Arc",
                MonsterDamageType.Electric => "Lightning Arc",
                MonsterDamageType.Pierce => "Force Arc",
                MonsterDamageType.Slash => "Blade Arc",
                MonsterDamageType.Harm => "Harm Other",
                MonsterDamageType.Nether or MonsterDamageType.VoidBasic =>
                    "Nether Arc",
                _ => null,
            },
            VtankCombatSpellType.Ring => element switch
            {
                MonsterDamageType.Acid => "Searing Disc",
                MonsterDamageType.Bludgeon => "Tectonic Rifts",
                MonsterDamageType.Cold => "Halo of Frost",
                MonsterDamageType.Fire => "Cassius' Ring of Fire",
                MonsterDamageType.Electric => "Eye of the Storm",
                MonsterDamageType.Pierce => "Nuhmudira's Spines",
                MonsterDamageType.Slash => "Horizon's Blades",
                MonsterDamageType.Harm => "Curse of Raven Fury",
                // Nether/VoidBasic: fk.cs:560 names spell id 5361 directly.
                _ => null,
            },
            VtankCombatSpellType.Streak => element switch
            {
                MonsterDamageType.Acid => "Acid Streak",
                MonsterDamageType.Bludgeon => "Shock Wave Streak",
                MonsterDamageType.Cold => "Frost Streak",
                MonsterDamageType.Fire => "Flame Streak",
                MonsterDamageType.Electric => "Lightning Streak",
                MonsterDamageType.Pierce => "Force Streak",
                MonsterDamageType.Slash => "Whirling Blade Streak",
                MonsterDamageType.Harm => "Harm Other",
                MonsterDamageType.Nether or MonsterDamageType.VoidBasic =>
                    "Nether Streak",
                _ => null,
            },
            _ => null,
        };

    public PluginSpellInfo? Resolve(
        MonsterDamageType element,
        VtankCombatSpellType type,
        Func<PluginSpellInfo, bool>? usable = null)
    {
        if (type == VtankCombatSpellType.Ring
            && element is MonsterDamageType.Nether or MonsterDamageType.VoidBasic)
        {
            return _byId.TryGetValue(VoidRingSpellId, out PluginSpellInfo voidRing)
                && usable?.Invoke(voidRing) != false
                ? voidRing
                : null;
        }
        string? family = FamilyName(element, type);
        return family is null ? null : ResolveFamily(family, usable);
    }

    public PluginSpellInfo? ResolveFamily(
        string familyName,
        Func<PluginSpellInfo, bool>? usable = null)
    {
        ArgumentNullException.ThrowIfNull(familyName);
        if (!_families.TryGetValue(familyName, out List<PluginSpellInfo>? members))
            return null;
        PluginSpellInfo? best = null;
        foreach (PluginSpellInfo spell in members)
        {
            if (best is { } current && spell.Quality <= current.Quality)
                continue;
            if (usable?.Invoke(spell) == false)
                continue;
            best = spell;
        }
        return best;
    }

    public PluginSpellInfo? ResolveTuskerFists() =>
        _byId.TryGetValue(TuskerFistsSpellId, out PluginSpellInfo tusker)
            ? tusker
            : ResolveFamily("Tusker Fists");

    public static MonsterDamageType ResolveMagicDamageMode(
        MonsterDamageType requested,
        ICharacterInfo character)
    {
        ArgumentNullException.ThrowIfNull(character);
        if (requested == MonsterDamageType.Prismatic)
            requested = MonsterDamageType.Auto;
        if (requested == MonsterDamageType.Fists)
            return MonsterDamageType.Bludgeon;
        if (requested != MonsterDamageType.Auto)
            return requested;
        if (IsTrained(character, WarMagicSkill))
            return MonsterDamageType.Auto;
        if (IsTrained(character, VoidMagicSkill))
            return MonsterDamageType.VoidBasic;
        return IsTrained(character, LifeMagicSkill)
            ? MonsterDamageType.DrainAuto
            : MonsterDamageType.Auto;
    }

    private static bool IsTrained(ICharacterInfo character, uint skillId) =>
        character.TryGetSkill(skillId, out PluginSkillInfo skill)
        && skill.Training is PluginSkillTraining.Trained
            or PluginSkillTraining.Specialized;

    internal static string BaseName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        const string incantation = "Incantation of ";
        ReadOnlySpan<char> span = name.AsSpan().Trim();
        if (span.StartsWith(incantation, StringComparison.OrdinalIgnoreCase))
            span = span[incantation.Length..].TrimStart();
        int space = span.LastIndexOf(' ');
        if (space > 0 && IsRomanNumeral(span[(space + 1)..]))
            span = span[..space].TrimEnd();
        return span.ToString();
    }

    private static bool IsRomanNumeral(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return false;
        foreach (char value in text)
        {
            if (value is not ('I' or 'V' or 'X'))
                return false;
        }
        return true;
    }
}
