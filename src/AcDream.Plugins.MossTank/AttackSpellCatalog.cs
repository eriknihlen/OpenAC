using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal enum AttackSpellShape
{
    Direct,
    Arc,
    Streak,
    Ring,
    Harm,
    Drain,
    Martyr,
}

internal readonly record struct AttackSpellChoice(
    PluginSpellInfo Spell,
    AttackSpellShape Shape,
    MonsterDamageType DamageType,
    bool CastWithoutTarget);

internal sealed class AttackSpellCatalog
{
    private const uint TuskerFistsSpellId = 0x0B76u;
    private readonly AttackSpellChoice[] _choices;

    private AttackSpellCatalog(AttackSpellChoice[] choices) =>
        _choices = choices;

    public static AttackSpellCatalog Build(
        IReadOnlyList<PluginSpellInfo> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var choices = new List<AttackSpellChoice>();
        foreach (PluginSpellInfo spell in spells)
        {
            if (TryClassify(spell, out AttackSpellChoice choice))
                choices.Add(choice);
        }
        return new AttackSpellCatalog([.. choices]);
    }

    public IReadOnlyList<AttackSpellChoice> Candidates(
        MonsterRuleActions actions,
        CombatSettings settings,
        PluginCombatTarget target,
        int nearbyRingTargets,
        ICharacterInfo character)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(character);

        MonsterDamageType damageMode = ResolveDamageMode(
            actions.DamageType,
            character);
        bool ringDue = actions.UsesRing
            && nearbyRingTargets >= (actions.UsesPrimaryAttack
                ? Math.Max(1, settings.MinimumRingTargets)
                : 1);
        var candidates = new List<AttackSpellChoice>();
        foreach (AttackSpellChoice choice in _choices)
        {
            if (!MatchesDamageMode(choice, damageMode))
                continue;
            if (choice.Shape == AttackSpellShape.Ring && !ringDue)
                continue;
            if (choice.Shape != AttackSpellShape.Ring
                && !MatchesPrimaryShape(choice.Shape, actions, settings, target))
            {
                continue;
            }
            candidates.Add(choice);
        }

        candidates.Sort((left, right) => Compare(
            left,
            right,
            actions with { DamageType = damageMode },
            settings,
            target,
            ringDue,
            character));
        return candidates;
    }

    private static int Compare(
        AttackSpellChoice left,
        AttackSpellChoice right,
        MonsterRuleActions actions,
        CombatSettings settings,
        PluginCombatTarget target,
        bool ringDue,
        ICharacterInfo character)
    {
        if (actions.DamageType == MonsterDamageType.Auto)
        {
            int leftDamage = VtankDamageDatabase.PreferenceIndex(
                target,
                left.DamageType);
            int rightDamage = VtankDamageDatabase.PreferenceIndex(
                target,
                right.DamageType);
            int damage = leftDamage.CompareTo(rightDamage);
            if (damage != 0)
                return damage;
        }

        int leftPreference = Preference(
            left.Shape, actions, settings, target, ringDue, character);
        int rightPreference = Preference(
            right.Shape, actions, settings, target, ringDue, character);
        int preferred = leftPreference.CompareTo(rightPreference);
        if (preferred != 0)
            return preferred;

        int tier = right.Spell.Tier.CompareTo(left.Spell.Tier);
        if (tier != 0)
            return tier;
        int difficulty = right.Spell.Difficulty.CompareTo(left.Spell.Difficulty);
        return difficulty != 0
            ? difficulty
            : left.Spell.SpellId.CompareTo(right.Spell.SpellId);
    }

    private static int Preference(
        AttackSpellShape shape,
        MonsterRuleActions actions,
        CombatSettings settings,
        PluginCombatTarget target,
        bool ringDue,
        ICharacterInfo character)
    {
        if (ringDue && shape == AttackSpellShape.Ring)
            return 0;

        if (actions.DamageType == MonsterDamageType.DrainAuto)
        {
            bool needsHealth = character.MaxHealth != 0u
                && character.CurrentHealth / (double)character.MaxHealth < 0.75d;
            if (needsHealth && shape == AttackSpellShape.Drain)
                return 1;
            if (!needsHealth
                && character.MaxHealth != 0u
                && character.CurrentHealth / (double)character.MaxHealth >= 0.5d
                && shape == AttackSpellShape.Martyr)
            {
                return 1;
            }
            return shape switch
            {
                AttackSpellShape.Drain => 2,
                AttackSpellShape.Martyr => 3,
                AttackSpellShape.Harm => 4,
                _ => 20,
            };
        }

        if (actions.UsesStreak)
        {
            if (shape == AttackSpellShape.Streak)
                return 1;
            if (ShouldUseArc(settings, target))
                return shape == AttackSpellShape.Arc ? 2 : 3;
            return shape == AttackSpellShape.Direct ? 2 : 3;
        }
        if (ShouldUseArc(settings, target))
        {
            if (shape == AttackSpellShape.Arc)
                return 1;
            if (shape == AttackSpellShape.Direct)
                return 2;
        }
        else
        {
            if (shape == AttackSpellShape.Direct)
                return 1;
            if (shape == AttackSpellShape.Arc)
                return 2;
        }

        return shape switch
        {
            AttackSpellShape.Harm => 1,
            AttackSpellShape.Streak => 3,
            AttackSpellShape.Arc => 4,
            AttackSpellShape.Direct => 5,
            _ => 10,
        };
    }

    private static bool ShouldUseArc(CombatSettings settings, PluginCombatTarget target) =>
        settings.UseArcs switch
        {
            UseArcsMode.Yes => true,
            UseArcsMode.AtRange => target.Distance >= settings.ArcRange,
            _ => false,
        };

    private static bool MatchesPrimaryShape(
        AttackSpellShape shape,
        MonsterRuleActions actions,
        CombatSettings settings,
        PluginCombatTarget target)
    {
        if (actions.DamageType == MonsterDamageType.DrainAuto)
        {
            return shape is AttackSpellShape.Drain
                or AttackSpellShape.Martyr
                or AttackSpellShape.Harm;
        }
        if (actions.DamageType == MonsterDamageType.Harm)
            return shape == AttackSpellShape.Harm;
        if (actions.UsesStreak)
        {
            // Streak is preferred, not a hard requirement: VTank falls back
            // when the matching streak/tier is unknown or presently gated.
            return shape is AttackSpellShape.Streak
                or AttackSpellShape.Direct
                or AttackSpellShape.Arc;
        }
        return shape is AttackSpellShape.Direct or AttackSpellShape.Arc;
    }

    private static bool MatchesDamageMode(
        AttackSpellChoice choice,
        MonsterDamageType requested)
    {
        return requested switch
        {
            MonsterDamageType.Harm => choice.Shape == AttackSpellShape.Harm,
            MonsterDamageType.DrainAuto => choice.Shape is AttackSpellShape.Drain
                or AttackSpellShape.Martyr
                or AttackSpellShape.Harm,
            MonsterDamageType.VoidBasic or MonsterDamageType.Nether =>
                choice.DamageType == MonsterDamageType.Nether,
            MonsterDamageType.Auto => choice.DamageType is not MonsterDamageType.Auto
                && choice.Shape is not (AttackSpellShape.Harm
                    or AttackSpellShape.Drain
                    or AttackSpellShape.Martyr),
            _ => choice.DamageType == requested,
        };
    }

    private static MonsterDamageType ResolveDamageMode(
        MonsterDamageType requested,
        ICharacterInfo character)
    {
        // VTank's ga/hi pair treats Prismatic as an ammunition policy while
        // retaining normal GameInfoDB element selection for magic.  Fists is
        // special only while the Tusker Fists enchantment is active;
        // otherwise ga resolves the attack element to Bludgeon.
        if (requested == MonsterDamageType.Prismatic)
            return MonsterDamageType.Auto;
        if (requested == MonsterDamageType.Fists)
        {
            return character.ActiveEnchantments.Any(
                    static enchantment => enchantment.SpellId == TuskerFistsSpellId)
                ? MonsterDamageType.Fists
                : MonsterDamageType.Bludgeon;
        }
        if (requested != MonsterDamageType.Auto)
            return requested;

        bool hasWar = IsTrained(character, 34u);
        if (hasWar)
            return MonsterDamageType.Auto;
        if (IsTrained(character, 43u))
            return MonsterDamageType.VoidBasic;
        return IsTrained(character, 33u)
            ? MonsterDamageType.DrainAuto
            : MonsterDamageType.Auto;
    }

    private static bool IsTrained(ICharacterInfo character, uint skillId) =>
        character.TryGetSkill(skillId, out PluginSkillInfo skill)
        && skill.Training is PluginSkillTraining.Trained
            or PluginSkillTraining.Specialized;

    internal static bool TryClassify(
        PluginSpellInfo spell,
        out AttackSpellChoice choice)
    {
        string name = Normalize(spell.Name);
        AttackSpellShape shape;
        MonsterDamageType damage;

        if (spell.SpellId == TuskerFistsSpellId
            || name.Equals("Tusker Fists", StringComparison.OrdinalIgnoreCase))
        {
            shape = AttackSpellShape.Direct;
            damage = MonsterDamageType.Fists;
        }
        else if (name.StartsWith("Harm Other", StringComparison.OrdinalIgnoreCase))
        {
            shape = AttackSpellShape.Harm;
            damage = MonsterDamageType.Harm;
        }
        else if (name.StartsWith(
            "Drain Health Other", StringComparison.OrdinalIgnoreCase))
        {
            shape = AttackSpellShape.Drain;
            damage = MonsterDamageType.DrainAuto;
        }
        else if (name.StartsWith(
            "Martyr's Hecatomb", StringComparison.OrdinalIgnoreCase))
        {
            shape = AttackSpellShape.Martyr;
            damage = MonsterDamageType.DrainAuto;
        }
        else
        {
            if (!spell.IsOffensive
                || spell.IsBeneficial
                || spell.IsDebuff
                || spell.IsDamageOverTime
                || DebuffSpellCatalog.TryClassify(spell, out _, out _))
            {
                choice = default;
                return false;
            }

            damage = DamageFromText(spell.Description, name);
            if (damage == MonsterDamageType.Auto)
            {
                choice = default;
                return false;
            }

            if (name.Contains(" Streak", StringComparison.OrdinalIgnoreCase))
                shape = AttackSpellShape.Streak;
            else if (name.Contains(" Arc", StringComparison.OrdinalIgnoreCase))
                shape = AttackSpellShape.Arc;
            else if ((spell.TargetMask == 0u || spell.IsUntargeted)
                && (name.Contains(" Ring", StringComparison.OrdinalIgnoreCase)
                    || spell.Description.Contains(
                        "outward from the caster",
                        StringComparison.OrdinalIgnoreCase)))
            {
                shape = AttackSpellShape.Ring;
            }
            else if (spell.TargetMask != 0u || spell.IsProjectile)
                shape = AttackSpellShape.Direct;
            else
            {
                choice = default;
                return false;
            }
        }

        choice = new AttackSpellChoice(
            spell,
            shape,
            damage,
            shape == AttackSpellShape.Ring
                || spell.IsUntargeted
                || spell.TargetMask == 0u);
        return true;
    }

    private static MonsterDamageType DamageFromText(
        string description,
        string name)
    {
        string text = string.Concat(description, " ", name);
        if (text.Contains("slashing damage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Blade", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Slash;
        if (text.Contains("piercing damage", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Pierce;
        if (text.Contains("bludgeoning damage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Shock Wave", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Bludgeon;
        if (text.Contains("cold damage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Frost", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Cold;
        if (text.Contains("fire damage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Flame", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Fire;
        if (text.Contains("acid damage", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Acid", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Acid;
        if (text.Contains("electric", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Lightning", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Electric;
        if (text.Contains("nether", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Nether;
        if (text.Contains("Force", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Pierce;
        return MonsterDamageType.Auto;
    }

    private static string Normalize(string name)
    {
        const string incantation = "Incantation of ";
        return name.StartsWith(incantation, StringComparison.OrdinalIgnoreCase)
            ? name[incantation.Length..]
            : name;
    }
}
