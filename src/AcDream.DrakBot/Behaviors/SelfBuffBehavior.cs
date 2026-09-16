using AcDream.DrakBot.Profiles;
using AcDream.DrakBot.Spells;
using AcDream.Plugin.Abstractions;

namespace AcDream.DrakBot.Behaviors;

/// <summary>
/// Keeps the configured self buffs up. A buff is due when no enchantment of
/// its family is active or the active one is about to expire. One cast per
/// step; the engine re-arbitrates between casts so a fight can interrupt.
/// Weapon auras are self-casts that land on the wielded weapon; armor
/// spells are cast on each equipped piece and judged by the client's record
/// of what the character landed on it, since item enchantments are not in
/// the player's own registry.
/// </summary>
public sealed class SelfBuffBehavior(
    SpellSelector spells,
    CastTracker casts,
    Func<BuffSettings> settings,
    IAutomationSurface? surface = null,
    Func<string>? wandName = null) : IBehavior
{
    private readonly MagicModeGate _magic = new(wandName ?? (() => string.Empty));
    private bool _forceRebuff;
    private readonly HashSet<uint> _forcedFamiliesDone = [];
    private uint _pendingSpell;
    private uint _pendingFamily;
    private uint _pendingTarget;

    public string Name => "buffs";

    private const double PendingWaitSeconds = 8d;
    private double _pendingSince = double.NaN;

    /// <summary>
    /// A cast the host calls a success, of a buff that is still due right
    /// after, this many times running is one that never lands: an Other
    /// tier sent with no target once "succeeded" nine thousand times in
    /// five minutes with the bot standing still. It is rested instead.
    /// </summary>
    public const int NoEffectStrikes = 3;
    public const double NoEffectRestSeconds = 120d;
    /// <summary>How soon after a success the same buff being due again counts as no effect; a real enchantment is in the registry by then.</summary>
    public const double NoEffectWindowSeconds = 3d;
    private uint _lastSucceededSpell;
    private double _lastSucceededAt = double.NegativeInfinity;
    private int _noEffectStrikes;

    /// <summary>Recast every configured buff once, regardless of time remaining.</summary>
    public void ForceRebuff()
    {
        _forceRebuff = true;
        _forcedFamiliesDone.Clear();
    }

    public bool IsForceRebuffPending => _forceRebuff;

    /// <summary>Drops a forced rebuff that has not finished; what is already recast stays.</summary>
    public void CancelForceRebuff()
    {
        _forceRebuff = false;
        _forcedFamiliesDone.Clear();
    }

    public BehaviorPriority Priority => BehaviorPriority.Buffing;

    public bool WantsControl(Blackboard board, out string reason)
    {
        reason = string.Empty;
        BuffSettings buffs = settings();
        if (!buffs.Enabled)
            return false;
        if (casts.HasPendingRequest)
        {
            reason = "finishing cast";
            return true;
        }
        if (TryNextDue(board, buffs, out PluginSpellInfo spell))
        {
            reason = $"{spell.Name} due";
            return true;
        }
        return false;
    }

    public BehaviorStep Execute(BehaviorContext context)
    {
        Blackboard board = context.Board;
        BuffSettings buffs = settings();

        CastOutcome? outcome = casts.Poll();
        if (casts.HasPendingRequest)
            return BehaviorStep.Continue;
        if (outcome is not null)
        {
            if (outcome == CastOutcome.Succeeded)
            {
                if (_forceRebuff)
                    _forcedFamiliesDone.Add(_pendingTarget == 0u ? _pendingFamily : FamilyOn(_pendingFamily, _pendingTarget));
                _lastSucceededSpell = _pendingSpell;
                _lastSucceededAt = board.Now;
            }
            return BehaviorStep.Done;
        }
        if (board.IsActionPending)
        {
            // A hand that never comes free is not waited on for ever: the
            // engine's watchdog clears the stuck action at ten seconds,
            // and the tick is given back before that so nothing else
            // starves meanwhile.
            if (double.IsNaN(_pendingSince))
                _pendingSince = board.Now;
            if (board.Now - _pendingSince < PendingWaitSeconds)
                return BehaviorStep.Continue;
            _pendingSince = double.NaN;
            return BehaviorStep.Fail("the client has had an action pending too long to wait on");
        }
        _pendingSince = double.NaN;

        if (!TryNextDue(board, buffs, out PluginSpellInfo spell, out uint target))
            return BehaviorStep.Done;
        if (spell.SpellId == _lastSucceededSpell && board.Now - _lastSucceededAt < NoEffectWindowSeconds)
        {
            if (++_noEffectStrikes >= NoEffectStrikes)
            {
                _noEffectStrikes = 0;
                casts.Rest(spell.SpellId, NoEffectRestSeconds);
                return BehaviorStep.Fail($"{spell.Name} was cast {NoEffectStrikes} times and never landed; resting it {NoEffectRestSeconds:0}s");
            }
        }
        else
        {
            _noEffectStrikes = 0;
        }
        if (!_magic.TryEnsure(context, out BehaviorStep mode))
            return mode;
        _pendingSpell = spell.SpellId;
        _pendingFamily = spell.Family;
        _pendingTarget = target;
        PluginCastRequestResult result = casts.Request(spell.SpellId, target);
        return result == PluginCastRequestResult.Sent
            ? BehaviorStep.Continue
            : BehaviorStep.Fail($"{spell.Name}: {result}");
    }

    public void Interrupt(BehaviorContext context)
    {
        casts.Clear();
        _magic.Reset();
    }

    /// <summary>
    /// One line per configured buff: what would be cast and whether it is
    /// due, or what stands in the way. For the /drakbot spells command.
    /// </summary>
    /// <summary>Why any spell the user names is, or is not, castable now - a heal as much as a buff.</summary>
    public string ExplainSpell(string name) => spells.Explain(name);

    public IEnumerable<string> Describe(Blackboard board)
    {
        BuffSettings buffs = settings();
        IReadOnlyList<PluginActiveEnchantment> registry = board.Enchantments;
        IReadOnlyList<PluginTrackedEnchantment> landed = surface?.Enchantments.Capture(board.SelfId) ?? [];
        foreach (string name in buffs.Spells)
        {
            string why = spells.Explain(name);
            if (!spells.TryBestSelfBuff(name, out PluginSpellInfo spell) && !spells.TryBestKnown(name, out spell))
            {
                yield return $"{name}: {why}";
                continue;
            }
            double remaining = double.NaN;
            foreach (PluginActiveEnchantment active in registry)
            {
                if (active.Family == spell.Family && (double.IsNaN(remaining) || active.SecondsRemaining > remaining))
                    remaining = active.SecondsRemaining;
            }
            foreach (PluginTrackedEnchantment tracked in landed)
            {
                if (tracked.Family == spell.Family && (double.IsNaN(remaining) || tracked.SecondsRemaining > remaining))
                    remaining = tracked.SecondsRemaining;
            }
            string state = double.IsNaN(remaining)
                ? "not up: due"
                : remaining > buffs.RebuffWhenRemainingSeconds
                    ? $"up, {remaining / 60d:0.0} min left"
                    : $"up, {remaining:0}s left: due";
            yield return $"{name}: {spell.Name}, {state}{(casts.IsOnCooldown(spell.SpellId) ? ", cooling down" : string.Empty)}";
        }
    }

    /// <summary>
    /// The state of every buff the profile asks for, as data: what each
    /// name resolved to, how long it has left (negative when not up),
    /// whether it is due by the profile's threshold, and why one cannot be
    /// cast. Self spells, the weapon auras when they are on, and the armor
    /// spells per worn piece when those are on - what a status window or
    /// a phone shows beside the raw enchantment list.
    /// </summary>
    public IReadOnlyList<BuffStatus> Report(Blackboard board)
    {
        ArgumentNullException.ThrowIfNull(board);
        BuffSettings buffs = settings();
        var report = new List<BuffStatus>();
        IReadOnlyList<PluginTrackedEnchantment> landed = surface?.Enchantments.Capture(board.SelfId) ?? [];
        foreach (string name in buffs.Spells)
            report.Add(SelfStatus(board, buffs, name, "self", landed));
        if (buffs.BuffWeapon)
        {
            foreach (string name in buffs.WeaponSpells)
                report.Add(SelfStatus(board, buffs, name, "weapon", landed));
        }
        if (buffs.BuffArmor && surface is not null)
        {
            foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
            {
                if (!item.IsEquipped || item.ObjectClass != PluginObjectClass.Armor)
                    continue;
                IReadOnlyList<PluginTrackedEnchantment>? onPiece = null;
                foreach (string name in buffs.ArmorSpells)
                {
                    if (!spells.TryBestKnown(name, out PluginSpellInfo candidate))
                    {
                        report.Add(new BuffStatus(name, "armor", null, 0u, 0u, 0, -1d, false, false, spells.Explain(name, buff: false), item.Name, item.ObjectId));
                        continue;
                    }
                    onPiece ??= surface.Enchantments.Capture(item.ObjectId);
                    double remaining = Remaining(onPiece, candidate.Family);
                    report.Add(new BuffStatus(
                        name, "armor", candidate.Name, candidate.SpellId, candidate.Family, candidate.Tier,
                        remaining, remaining <= buffs.RebuffWhenRemainingSeconds, casts.IsOnCooldown(candidate.SpellId), null,
                        item.Name, item.ObjectId));
                }
            }
        }
        return report;
    }

    private BuffStatus SelfStatus(Blackboard board, BuffSettings buffs, string name, string kind, IReadOnlyList<PluginTrackedEnchantment> landed)
    {
        if (!spells.TryBestSelfBuff(name, out PluginSpellInfo spell) && !spells.TryBestKnown(name, out spell))
            return new BuffStatus(name, kind, null, 0u, 0u, 0, -1d, false, false, spells.Explain(name), null, 0u);
        double remaining = -1d;
        int tierUp = 0;
        foreach (PluginActiveEnchantment active in board.Enchantments)
        {
            if (active.Family == spell.Family && active.SecondsRemaining > remaining)
            {
                remaining = active.SecondsRemaining;
                tierUp = active.Tier;
            }
        }
        double onRecord = Remaining(landed, spell.Family);
        if (onRecord > remaining)
            remaining = onRecord;
        // A higher tier already in force outranks what we can cast, as the behavior judges it.
        bool covered = remaining > buffs.RebuffWhenRemainingSeconds || tierUp > spell.Tier;
        return new BuffStatus(
            name, kind, spell.Name, spell.SpellId, spell.Family, spell.Tier,
            remaining, !covered, casts.IsOnCooldown(spell.SpellId), null, null, 0u);
    }

    private static double Remaining(IReadOnlyList<PluginTrackedEnchantment> landed, uint family)
    {
        double remaining = -1d;
        foreach (PluginTrackedEnchantment enchantment in landed)
        {
            if (enchantment.Family == family && enchantment.SecondsRemaining > remaining)
                remaining = enchantment.SecondsRemaining;
        }
        return remaining;
    }

    /// <summary>Whether any configured buff is missing or expiring, for a meta's NeedToBuff.</summary>
    public bool NeedsAnyBuff(Blackboard board) => TryNextDue(board, settings(), out _, out _);

    internal bool TryNextDue(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell) =>
        TryNextDue(board, buffs, out spell, out _);

    /// <summary>
    /// The first configured buff that is missing or expiring and can be
    /// cast now, with the item it is cast on (zero for the character).
    /// </summary>
    internal bool TryNextDue(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out uint target)
    {
        target = 0u;
        foreach (string name in buffs.Spells)
        {
            if (TryDueSelf(board, buffs, name, out spell))
                return true;
        }
        if (buffs.BuffWeapon)
        {
            foreach (string name in buffs.WeaponSpells)
            {
                if (TryDueSelf(board, buffs, name, out spell))
                    return true;
            }
        }
        if (buffs.BuffArmor && surface is not null && TryDueArmor(board, buffs, out spell, out target))
            return true;
        // Nothing left to force: the pass is over.
        _forceRebuff = false;
        spell = default;
        return false;
    }

    private bool TryDueSelf(Blackboard board, BuffSettings buffs, string name, out PluginSpellInfo spell)
    {
        spell = default;
        if (!spells.TryBestSelfBuff(name, out PluginSpellInfo candidate) && !spells.TryBestKnown(name, out candidate))
            return false;
        if (casts.IsOnCooldown(candidate.SpellId))
            return false;
        bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(candidate.Family);
        if (!forced && IsCovered(board.Enchantments, candidate, buffs.RebuffWhenRemainingSeconds))
            return false;
        // An aura the registry does not list may still be on record as landed on the character.
        if (!forced && surface is not null
            && IsCovered(surface.Enchantments.Capture(board.SelfId), candidate, buffs.RebuffWhenRemainingSeconds))
            return false;
        spell = candidate;
        return true;
    }

    private bool TryDueArmor(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out uint target)
    {
        spell = default;
        target = 0u;
        foreach (PluginInventoryItem item in surface!.Items.CaptureOwnedItems())
        {
            if (!item.IsEquipped || item.ObjectClass != PluginObjectClass.Armor)
                continue;
            IReadOnlyList<PluginTrackedEnchantment>? landed = null;
            foreach (string name in buffs.ArmorSpells)
            {
                if (!spells.TryBestKnown(name, out PluginSpellInfo candidate) || casts.IsOnCooldown(candidate.SpellId))
                    continue;
                bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(FamilyOn(candidate.Family, item.ObjectId));
                landed ??= surface.Enchantments.Capture(item.ObjectId);
                if (!forced && IsCovered(landed, candidate, buffs.RebuffWhenRemainingSeconds))
                    continue;
                spell = candidate;
                target = item.ObjectId;
                return true;
            }
        }
        return false;
    }

    /// <summary>A forced pass tracks armor spells per piece, not per family.</summary>
    private static uint FamilyOn(uint family, uint objectId) => unchecked(family * 2654435761u ^ objectId);

    private static bool IsCovered(
        IReadOnlyList<PluginTrackedEnchantment> landed,
        in PluginSpellInfo spell,
        double rebuffWhenRemaining)
    {
        foreach (PluginTrackedEnchantment enchantment in landed)
        {
            if (enchantment.Family == spell.Family && enchantment.SecondsRemaining > rebuffWhenRemaining)
                return true;
        }
        return false;
    }

    private static bool IsCovered(
        IReadOnlyList<PluginActiveEnchantment> active,
        in PluginSpellInfo spell,
        double rebuffWhenRemaining)
    {
        foreach (PluginActiveEnchantment enchantment in active)
        {
            if (enchantment.Family != spell.Family)
                continue;
            // A higher tier already in force outranks what we can cast.
            if (enchantment.Tier > spell.Tier)
                return true;
            if (enchantment.SecondsRemaining > rebuffWhenRemaining)
                return true;
        }
        return false;
    }
}

/// <summary>
/// One line of <see cref="SelfBuffBehavior.Report"/>: a buff the profile
/// asks for (<paramref name="Configured"/>, the name as written), what it
/// resolved to, and how it stands. <paramref name="SecondsRemaining"/> is
/// negative when nothing of the family is up; <paramref name="Problem"/>
/// says why a name could not be resolved. Armor entries name the piece.
/// </summary>
public sealed record BuffStatus(
    string Configured,
    string Kind,
    string? SpellName,
    uint SpellId,
    uint Family,
    int Tier,
    double SecondsRemaining,
    bool Due,
    bool OnCooldown,
    string? Problem,
    string? ItemName,
    uint ItemId)
{
    public bool IsUp => SecondsRemaining >= 0d;
}
