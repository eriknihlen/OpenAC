using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal readonly record struct DebuffIdentity(
    MonsterActionFlags Flag,
    MonsterDamageType DamageType);

internal readonly record struct DebuffChoice(
    DebuffIdentity Identity,
    PluginSpellInfo Spell,
    int ActionOrder);

internal sealed class DebuffSpellCatalog
{
    private static readonly (MonsterActionFlags Flag, int Order)[] OrderedFlags =
    [
        (MonsterActionFlags.Fester, 0),
        (MonsterActionFlags.Broadside, 1),
        (MonsterActionFlags.GravityWell, 2),
        (MonsterActionFlags.Imperil, 3),
        (MonsterActionFlags.Yield, 4),
        (MonsterActionFlags.Vulnerability, 5),
        (MonsterActionFlags.WeakeningCurse, 6),
        (MonsterActionFlags.FesteringCurse, 7),
        (MonsterActionFlags.Corruption, 8),
        (MonsterActionFlags.DestructiveCurse, 9),
        (MonsterActionFlags.Corrosion, 10),
    ];

    private readonly DebuffChoice[] _choices;

    private DebuffSpellCatalog(DebuffChoice[] choices) => _choices = choices;

    public static DebuffSpellCatalog Build(IReadOnlyList<PluginSpellInfo> spells)
    {
        ArgumentNullException.ThrowIfNull(spells);
        var choices = new List<DebuffChoice>();
        foreach (PluginSpellInfo spell in spells)
        {
            if (!TryClassify(spell, out DebuffIdentity identity, out int order))
                continue;
            choices.Add(new DebuffChoice(identity, spell, order));
        }
        return new DebuffSpellCatalog([.. choices]);
    }

    public IReadOnlyList<DebuffChoice> Candidates(
        MonsterRuleActions actions,
        DebuffSelectionMethod selection,
        ICharacterInfo character,
        Func<DebuffIdentity, PluginSpellInfo, bool> isDue)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(isDue);

        HashSet<DebuffIdentity> required = Required(actions);
        if (required.Count == 0 || _choices.Length == 0)
            return Array.Empty<DebuffChoice>();

        var candidates = new List<DebuffChoice>();
        foreach (DebuffChoice choice in _choices)
        {
            if (required.Contains(choice.Identity)
                && isDue(choice.Identity, choice.Spell))
            {
                candidates.Add(choice);
            }
        }

        candidates.Sort((left, right) => Compare(
            left, right, selection, character));
        return candidates;
    }

    public bool HasKnownRequirement(MonsterRuleActions actions)
    {
        HashSet<DebuffIdentity> required = Required(actions);
        foreach (DebuffChoice choice in _choices)
        {
            if (required.Contains(choice.Identity))
                return true;
        }
        return false;
    }

    private static int Compare(
        DebuffChoice left,
        DebuffChoice right,
        DebuffSelectionMethod selection,
        ICharacterInfo character)
    {
        if (selection == DebuffSelectionMethod.Skill)
        {
            uint leftSkill = Skill(character, left.Spell.School);
            uint rightSkill = Skill(character, right.Spell.School);
            int skill = rightSkill.CompareTo(leftSkill);
            if (skill != 0)
                return skill;
        }

        int tier = right.Spell.Tier.CompareTo(left.Spell.Tier);
        if (tier != 0)
            return tier;
        int difficulty = right.Spell.Difficulty.CompareTo(left.Spell.Difficulty);
        if (difficulty != 0)
            return difficulty;
        int action = left.ActionOrder.CompareTo(right.ActionOrder);
        return action != 0
            ? action
            : left.Spell.SpellId.CompareTo(right.Spell.SpellId);
    }

    private static uint Skill(ICharacterInfo character, uint skillId) =>
        character.TryGetSkill(skillId, out PluginSkillInfo skill)
            ? skill.Current
            : 0u;

    internal static HashSet<DebuffIdentity> Required(MonsterRuleActions actions)
    {
        var required = new HashSet<DebuffIdentity>();
        foreach ((MonsterActionFlags flag, _) in OrderedFlags)
        {
            if ((actions.Flags & flag) == 0)
                continue;
            MonsterDamageType damage = flag == MonsterActionFlags.Vulnerability
                ? actions.DamageType
                : MonsterDamageType.Auto;
            required.Add(new DebuffIdentity(flag, damage));
        }

        if ((actions.Flags & MonsterActionFlags.Vulnerability) != 0
            && actions.ExtraVulnerability != MonsterDamageType.Auto)
        {
            required.Add(new DebuffIdentity(
                MonsterActionFlags.Vulnerability,
                actions.ExtraVulnerability));
        }
        return required;
    }

    internal static bool TryClassify(
        PluginSpellInfo spell,
        out DebuffIdentity identity,
        out int order)
    {
        string name = Normalize(spell.Name);
        MonsterActionFlags flag;
        MonsterDamageType damage = MonsterDamageType.Auto;

        if (name.StartsWith("Fester Other", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Fester;
        else if (name.StartsWith("Broadside of a Barn", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Broadside;
        else if (name.StartsWith("Gravity Well", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.GravityWell;
        else if (name.StartsWith("Imperil Other", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Imperil;
        else if (name.StartsWith("Magic Yield Other", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Yield;
        else if (name.Contains(" Vulnerability Other", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Vulnerability Other", StringComparison.OrdinalIgnoreCase)
            || IsClassicLure(name))
        {
            flag = MonsterActionFlags.Vulnerability;
            damage = DamageFromName(name);
        }
        else if (name.StartsWith("Weakening Curse", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.WeakeningCurse;
        else if (name.StartsWith("Festering Curse", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.FesteringCurse;
        else if (name.StartsWith("Corruption", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Corruption;
        else if (name.StartsWith("Destructive Curse", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.DestructiveCurse;
        else if (name.StartsWith("Corrosion", StringComparison.OrdinalIgnoreCase))
            flag = MonsterActionFlags.Corrosion;
        else
        {
            identity = default;
            order = int.MaxValue;
            return false;
        }

        order = Array.FindIndex(
            OrderedFlags,
            entry => entry.Flag == flag);
        if (order < 0)
            order = int.MaxValue;
        identity = new DebuffIdentity(flag, damage);
        return true;
    }

    private static string Normalize(string name)
    {
        const string incantation = "Incantation of ";
        return name.StartsWith(incantation, StringComparison.OrdinalIgnoreCase)
            ? name[incantation.Length..]
            : name;
    }

    private static bool IsClassicLure(string name) =>
        name.StartsWith("Acid Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Blade Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Bludgeon Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Flame Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Frost Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Lightning Lure", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Piercing Lure", StringComparison.OrdinalIgnoreCase);

    internal static MonsterDamageType DamageFromName(string name)
    {
        if (name.Contains("Blade", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Slash;
        if (name.Contains("Piercing", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Pierce;
        if (name.Contains("Bludgeon", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Bludgeon;
        if (name.Contains("Cold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Frost", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Cold;
        if (name.Contains("Fire", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Flame", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Fire;
        if (name.Contains("Acid", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Acid;
        if (name.Contains("Lightning", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Electric;
        if (name.Contains("Nether", StringComparison.OrdinalIgnoreCase))
            return MonsterDamageType.Nether;
        return MonsterDamageType.Auto;
    }
}

/// <summary>
/// Session-local VTank spell tracker. A debuff becomes active only after the
/// host publishes its matching server UseDone receipt.
/// </summary>
internal sealed class DebuffTracker
{
    private readonly Dictionary<(uint Target, DebuffIdentity Identity), Applied> _applied = [];
    private Pending? _pending;
    private long _observedCompletionRevision;

    public bool HasPending => _pending is not null;
    public string PendingName => _pending?.Spell.Name ?? string.Empty;
    public uint PendingTarget => _pending?.TargetObjectId ?? 0u;

    public bool IsDue(
        uint targetObjectId,
        DebuffIdentity identity,
        PluginSpellInfo spell,
        double now,
        double precastSeconds)
    {
        if (!_applied.TryGetValue((targetObjectId, identity), out Applied applied))
            return true;
        if (applied.SpellId != spell.SpellId && spell.Tier > applied.Tier)
            return true;
        double lead = spell.IsDamageOverTime ? 0d : Math.Max(0d, precastSeconds);
        return now >= applied.ExpiresAt - lead;
    }

    public void Begin(
        uint targetObjectId,
        DebuffIdentity identity,
        PluginSpellInfo spell,
        double now,
        long completionRevision)
    {
        _observedCompletionRevision = Math.Max(
            _observedCompletionRevision,
            completionRevision);
        _pending = new Pending(targetObjectId, identity, spell, now);
    }

    public DebuffCompletion Observe(
        PluginCastCompletion completion,
        double now)
    {
        if (completion.Revision <= _observedCompletionRevision)
            return default;
        _observedCompletionRevision = completion.Revision;
        if (_pending is not { } pending
            || pending.Spell.SpellId != completion.SpellId
            || pending.TargetObjectId != completion.TargetObjectId)
        {
            return default;
        }

        _pending = null;
        if (!completion.IsSuccess)
        {
            return new DebuffCompletion(
                Completed: true,
                Succeeded: false,
                pending.Spell.Name,
                completion.WeenieError);
        }

        double duration = Math.Max(0d, pending.Spell.DurationSeconds);
        _applied[(pending.TargetObjectId, pending.Identity)] = new Applied(
            pending.Spell.SpellId,
            pending.Spell.Tier,
            now + duration);
        return new DebuffCompletion(
            Completed: true,
            Succeeded: true,
            pending.Spell.Name,
            0u);
    }

    public bool ExpirePending(double now, double timeoutSeconds = 15d)
    {
        if (_pending is not { } pending
            || now - pending.DispatchedAt < timeoutSeconds)
        {
            return false;
        }
        _pending = null;
        return true;
    }

    public void RecordApplied(
        uint targetObjectId,
        DebuffIdentity identity,
        PluginSpellInfo spell,
        double now)
    {
        double duration = Math.Max(0d, spell.DurationSeconds);
        _applied[(targetObjectId, identity)] = new Applied(
            spell.SpellId,
            spell.Tier,
            now + duration);
    }

    /// <summary>
    /// VTank's <c>/vt fakeimp</c> records Gossamer Flesh locally for 3,000
    /// seconds. It is deliberately stronger than every learnable Imperil tier
    /// so the debug marker remains authoritative for its requested duration.
    /// </summary>
    public void RecordFakeImperil(uint targetObjectId, double now)
    {
        const uint gossamerFlesh = 0x081Au;
        const double durationSeconds = 3000d;
        _applied[(targetObjectId, new DebuffIdentity(
            MonsterActionFlags.Imperil,
            MonsterDamageType.Auto))] = new Applied(
                gossamerFlesh,
                int.MaxValue,
                now + durationSeconds);
    }

    public void ClearPending() => _pending = null;

    public void RetainTargets(IReadOnlySet<uint> liveTargets)
    {
        if (_applied.Count == 0)
            return;
        foreach ((uint Target, DebuffIdentity Identity) key in _applied.Keys.ToArray())
        {
            if (!liveTargets.Contains(key.Target))
                _applied.Remove(key);
        }
    }

    public void Reset()
    {
        _applied.Clear();
        _pending = null;
        _observedCompletionRevision = 0;
    }

    private readonly record struct Pending(
        uint TargetObjectId,
        DebuffIdentity Identity,
        PluginSpellInfo Spell,
        double DispatchedAt);

    private readonly record struct Applied(
        uint SpellId,
        int Tier,
        double ExpiresAt);
}

internal readonly record struct DebuffCompletion(
    bool Completed,
    bool Succeeded,
    string SpellName,
    uint WeenieError);
