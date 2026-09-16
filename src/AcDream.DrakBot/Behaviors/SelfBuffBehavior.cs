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
    /// <summary>The cast in flight is an armor spell, cast on the character for every worn piece.</summary>
    private bool _pendingArmor;

    // ── armor ──────────────────────────────────────────────────────────
    // What a worn piece has on it is only ever learnt by asking the server
    // about the piece: an item enchantment never reaches the character's
    // own registry, and the appraisal lists what is on the item without a
    // time. So a piece is appraised when the answer is older than this,
    // and judged by the appraisal - present or not - with the client's
    // own record of what the bot landed on it, which does carry a time,
    // taking precedence while it lasts. A buff that lapses is seen within
    // one appraisal and put back; that is the most a retail client can
    // know, and it is enough.
    public const double ArmorAppraiseSeconds = 120d;
    /// <summary>The most an appraisal is waited for before it is asked for again.</summary>
    public const double AppraiseWaitSeconds = 4d;
    private uint _appraising;
    private double _appraiseSentAt = double.NegativeInfinity;

    private enum ArmorNeed { None, Appraise, Cast }

    /// <summary>
    /// An armor spell is cast on the character, not on a piece: the server
    /// redirects an item spell cast at a creature to every equipped item
    /// of the spell's kind - armor and clothing alike for Impenetrability
    /// and the Banes - so one cast dresses the whole set, as a player's
    /// does. What each piece then has on it is read off its appraisal.
    /// </summary>
    private static bool IsWornVestment(in PluginInventoryItem item) =>
        item.IsEquipped && item.ObjectClass is PluginObjectClass.Armor or PluginObjectClass.Clothing;

    // ── one pass, not one buff at a time ─────────────────────────────────
    // A buff crossing the minute brings the wand out; once it is out, every
    // buff that would cross it in the next twenty minutes goes in the same
    // pass, and the sword comes back once. The pass is open from the first
    // cast until nothing is due at the wide threshold.
    private bool _passOpen;
    /// <summary>Families cast this pass: judged at the profile's own threshold, not the wide one, so a short buff's fresh duration is not "due" again in the pass it was cast in.</summary>
    private readonly HashSet<uint> _castThisPass = [];

    /// <summary>The seconds-left threshold a family is due at: the pass's wide one, unless the family was cast in this pass.</summary>
    private double DueThreshold(BuffSettings buffs, uint family) =>
        _castThisPass.Contains(family) ? buffs.RebuffWhenRemainingSeconds : DueThreshold(buffs);

    /// <summary>The seconds-left threshold a buff is due at: the profile's, or the wide one while a pass is open.</summary>
    private double DueThreshold(BuffSettings buffs) =>
        _passOpen ? Math.Max(buffs.RebuffWhenRemainingSeconds, buffs.RebuffTogetherWithinSeconds) : buffs.RebuffWhenRemainingSeconds;

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
        if (buffs.BuffArmor && surface is not null && NextArmorNeed(board, buffs, out _, out PluginInventoryItem piece) == ArmorNeed.Appraise)
        {
            reason = $"asking about {piece.Name}";
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
                    _forcedFamiliesDone.Add(_pendingFamily);
                _lastSucceededSpell = _pendingSpell;
                _lastSucceededAt = board.Now;
                // An armor spell landed on the character: the record says so
                // for every worn piece, with the spell's own duration, until
                // the next appraisal of each says what really took.
                if (_pendingArmor && surface is not null && surface.Spells.TryGet(_pendingSpell, out PluginSpellInfo cast))
                {
                    foreach (PluginInventoryItem item in surface.Items.CaptureOwnedItems())
                    {
                        if (IsWornVestment(item))
                            surface.Enchantments.ReportCast(item.ObjectId, _pendingSpell, cast.DurationSeconds);
                    }
                }
                _pendingArmor = false;
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
        {
            if (buffs.BuffArmor && surface is not null && NextArmorNeed(board, buffs, out _, out PluginInventoryItem piece) == ArmorNeed.Appraise)
                return Appraise(board, piece);
            _passOpen = false;
            _castThisPass.Clear();
            return BehaviorStep.Done;
        }
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
        _pendingArmor = _armorCast;
        _armorCast = false;
        _passOpen = true;
        _castThisPass.Add(spell.Family);
        PluginCastRequestResult result = casts.Request(spell.SpellId, target);
        return result == PluginCastRequestResult.Sent
            ? BehaviorStep.Continue
            : BehaviorStep.Fail($"{spell.Name}: {result}");
    }

    public void Interrupt(BehaviorContext context)
    {
        casts.Clear();
        _magic.Reset();
        _appraising = 0u;
    }

    /// <summary>Asks the server about a worn piece, once, and waits for the answer to arrive.</summary>
    private BehaviorStep Appraise(Blackboard board, in PluginInventoryItem piece)
    {
        if (_appraising == piece.ObjectId && board.Now - _appraiseSentAt < AppraiseWaitSeconds)
            return BehaviorStep.Continue;
        PluginItemCommandResult result = surface!.Items.Appraise(piece.ObjectId);
        if (result.Status == PluginItemCommandStatus.Busy)
            return BehaviorStep.Continue;
        if (!result.Accepted)
            return BehaviorStep.Fail($"cannot ask about {piece.Name}: {result.Status} {result.Notice}".TrimEnd());
        _appraising = piece.ObjectId;
        _appraiseSentAt = board.Now;
        return BehaviorStep.Continue;
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
                if (!IsWornVestment(item))
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
                    bool fresh = IsAppraisalFresh(item);
                    bool present = fresh && AppraisalShows(item, candidate);
                    // On record with a time: that time. Seen on the piece by
                    // the appraisal, time unknown: up, not due. Not asked yet:
                    // not due either - asked first.
                    bool due = remaining >= 0d
                        ? remaining <= buffs.RebuffWhenRemainingSeconds
                        : fresh && !present;
                    string? problem = fresh || remaining >= 0d ? null : "not asked about yet";
                    report.Add(new BuffStatus(
                        name, "armor", candidate.Name, candidate.SpellId, candidate.Family, candidate.Tier,
                        remaining, due, casts.IsOnCooldown(candidate.SpellId), problem,
                        item.Name, item.ObjectId)
                    {
                        IsUpUntimed = remaining < 0d && present,
                    });
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
        if (!forced && IsCovered(board.Enchantments, candidate, DueThreshold(buffs, candidate.Family)))
            return false;
        // An aura the registry does not list may still be on record as landed on the character.
        if (!forced && surface is not null
            && IsCovered(surface.Enchantments.Capture(board.SelfId), candidate, DueThreshold(buffs, candidate.Family)))
            return false;
        spell = candidate;
        return true;
    }

    private bool _armorCast;

    private bool TryDueArmor(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out uint target)
    {
        target = 0u;
        if (NextArmorNeed(board, buffs, out spell, out _) != ArmorNeed.Cast)
            return false;
        // Cast on the character: the server dresses every worn piece with it.
        target = board.SelfId;
        _armorCast = true;
        return true;
    }

    /// <summary>
    /// The next thing the armor wants: a spell to cast on a piece the
    /// appraisal and the record agree is without it, else a piece whose
    /// appraisal is missing or old. Casts come first, so a pass over the
    /// set is not held up by asking.
    /// </summary>
    private ArmorNeed NextArmorNeed(Blackboard board, BuffSettings buffs, out PluginSpellInfo spell, out PluginInventoryItem piece)
    {
        spell = default;
        piece = default;
        PluginInventoryItem? stale = null;
        var worn = new List<PluginInventoryItem>();
        foreach (PluginInventoryItem item in surface!.Items.CaptureOwnedItems())
        {
            if (!IsWornVestment(item))
                continue;
            worn.Add(item);
            bool fresh = IsAppraisalFresh(item);
            if (!fresh && stale is null && (_appraising != item.ObjectId || board.Now - _appraiseSentAt >= AppraiseWaitSeconds || item.AppraisalAgeSeconds < 0d))
                stale = item;
        }
        if (worn.Count > 0)
        {
            foreach (string name in buffs.ArmorSpells)
            {
                if (!spells.TryBestKnown(name, out PluginSpellInfo candidate) || casts.IsOnCooldown(candidate.SpellId))
                    continue;
                bool forced = _forceRebuff && !_forcedFamiliesDone.Contains(candidate.Family);
                // One cast dresses every piece; it is due when any worn piece
                // is without the family - by the record's time where the record
                // has it, else by what the appraisal showed on the piece.
                bool due = forced;
                foreach (PluginInventoryItem item in worn)
                {
                    if (due)
                        break;
                    double onRecord = Remaining(surface.Enchantments.Capture(item.ObjectId), candidate.Family);
                    if (onRecord > DueThreshold(buffs, candidate.Family))
                        continue;
                    if (onRecord < 0d && (!IsAppraisalFresh(item) || AppraisalShows(item, candidate)))
                        continue;
                    due = true;
                    piece = item;
                }
                if (!due)
                    continue;
                spell = candidate;
                return ArmorNeed.Cast;
            }
        }
        if (stale is { } toAsk)
        {
            piece = toAsk;
            return ArmorNeed.Appraise;
        }
        return ArmorNeed.None;
    }

    private static bool IsAppraisalFresh(in PluginInventoryItem item) =>
        item.AppraisalAgeSeconds >= 0d && item.AppraisalAgeSeconds <= ArmorAppraiseSeconds;

    /// <summary>Whether the last appraisal of the piece listed an enchantment of the spell's family at its tier or better.</summary>
    private bool AppraisalShows(in PluginInventoryItem item, in PluginSpellInfo candidate)
    {
        foreach (uint listed in item.AppraisedSpellIds)
        {
            if ((listed & PluginInventoryItem.ActiveEnchantmentMask) == 0u)
                continue;
            uint spellId = listed & ~PluginInventoryItem.ActiveEnchantmentMask;
            if (spellId == candidate.SpellId)
                return true;
            if (surface!.Spells.TryGet(spellId, out PluginSpellInfo onPiece)
                && onPiece.Family == candidate.Family && onPiece.Tier >= candidate.Tier)
            {
                return true;
            }
        }
        return false;
    }

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
    /// <summary>Seen on the piece by an appraisal, which carries no time: up, with <see cref="SecondsRemaining"/> negative.</summary>
    public bool IsUpUntimed { get; init; }

    public bool IsUp => SecondsRemaining >= 0d || IsUpUntimed;
}
