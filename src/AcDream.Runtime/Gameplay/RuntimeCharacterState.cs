using AcDream.Core.Player;
using AcDream.Core.Net.Messages;
using AcDream.Core.Spells;
using AcDream.Core.Items;
using AcDream.Core.Properties;

namespace AcDream.Runtime.Gameplay;

public readonly record struct RuntimeCharacterOwnershipSnapshot(
    bool IsDisposed,
    bool InternalSubscriptionsAttached,
    int LearnedSpellCount,
    int ActiveEnchantmentCount,
    int DesiredComponentCount,
    int FavoriteSpellCount,
    int VitalCount,
    int AttributeCount,
    int SkillCount,
    int PositionCount,
    int PropertyCount,
    bool OptionsAreDefaults,
    bool MovementSkillsAreReset,
    bool AutonomyIsDefault = true,
    bool OptionsAreClean = true,
    int TitleCount = 0,
    bool DisplayTitleIsDefault = true)
{
    public bool IsConverged =>
        IsDisposed
        && !InternalSubscriptionsAttached
        && LearnedSpellCount == 0
        && ActiveEnchantmentCount == 0
        && DesiredComponentCount == 0
        && FavoriteSpellCount == 0
        && VitalCount == 0
        && AttributeCount == 0
        && SkillCount == 0
        && PositionCount == 0
        && PropertyCount == 0
        && OptionsAreDefaults
        && MovementSkillsAreReset
        && AutonomyIsDefault
        && OptionsAreClean
        && TitleCount == 0
        && DisplayTitleIsDefault;
}

public sealed class RuntimeCharacterState : IDisposable
{
    public const uint RunSkillId = 24u;
    public const uint JumpSkillId = 22u;
    public const uint FullAutonomyLevel = 2u;

    private bool _disposed;
    private long _characterRevision;
    private long _spellbookRevision;
    private bool _internalSubscriptionsAttached;
    private uint _autonomyLevel = FullAutonomyLevel;

    private int _runSkillBase = -1;
    private int _jumpSkillBase = -1;
    private PlayerSkillMath.AugmentationBonuses _movementSkillAugmentations;

    public RuntimeCharacterState(
        SpellTable? spellTable = null,
        TimeProvider? timeProvider = null)
    {
        Spellbook = new Spellbook(spellTable);
        LocalPlayer = new LocalPlayerState(Spellbook);
        Options = new RuntimeCharacterOptionsState(timeProvider);
        MovementSkills = new RuntimeMovementSkillState();
        Titles = new RuntimeCharacterTitleState();
        View = new CharacterView(this);
        Spellbook.StateChanged += OnSpellbookChanged;
        Spellbook.EnchantmentsChanged += OnEnchantmentsChangedForMovement;
        LocalPlayer.Changed += OnVitalChanged;
        LocalPlayer.AttributeChanged += OnAttributeChanged;
        LocalPlayer.CharacterChanged += OnCharacterChanged;
        _internalSubscriptionsAttached = true;
    }

    public Spellbook Spellbook { get; }
    public LocalPlayerState LocalPlayer { get; }
    public RuntimeCharacterOptionsState Options { get; }
    public RuntimeMovementSkillState MovementSkills { get; }
    public RuntimeCharacterTitleState Titles { get; }
    public IRuntimeCharacterView View { get; }
    public bool IsDisposed => _disposed;

    public bool IsOlthoiPlayer
    {
        get
        {
            int heritage = LocalPlayer.Properties.GetInt((uint)PropertyInt.HeritageGroup, 0);
            return heritage == 12 || heritage == 13; // HeritageGroup.Olthoi / OlthoiAcid
        }
    }

    public uint AutonomyLevel => Volatile.Read(ref _autonomyLevel);

    public bool UsePositionFromServer => AutonomyLevel != FullAutonomyLevel;

    public bool TrySetAutonomyLevel(uint level)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (level > FullAutonomyLevel)
            return false;
        Volatile.Write(ref _autonomyLevel, level);
        return true;
    }

    public RuntimeCharacterOwnershipSnapshot CaptureOwnership()
    {
        int favoriteCount = 0;
        for (int tab = 0; tab < 8; tab++)
            favoriteCount += Spellbook.GetFavorites(tab).Count;

        int vitalCount = 0;
        foreach (LocalPlayerState.VitalKind kind
            in Enum.GetValues<LocalPlayerState.VitalKind>())
        {
            if (LocalPlayer.Get(kind) is not null)
                vitalCount++;
        }

        int attributeCount = 0;
        foreach (LocalPlayerState.AttributeKind kind
            in Enum.GetValues<LocalPlayerState.AttributeKind>())
        {
            if (LocalPlayer.GetAttribute(kind) is not null)
                attributeCount++;
        }

        PropertyBundle properties = LocalPlayer.Properties;
        int propertyCount =
            properties.Bools.Count
            + properties.Ints.Count
            + properties.Int64s.Count
            + properties.Floats.Count
            + properties.Strings.Count
            + properties.DataIds.Count
            + properties.InstanceIds.Count;
        RuntimeCharacterOptionsSnapshot options = Options.Snapshot;
        return new RuntimeCharacterOwnershipSnapshot(
            _disposed,
            _internalSubscriptionsAttached,
            Spellbook.LearnedCount,
            Spellbook.ActiveCount,
            Spellbook.DesiredComponents.Count,
            favoriteCount,
            vitalCount,
            attributeCount,
            LocalPlayer.Skills.Count,
            LocalPlayer.Positions.Count,
            propertyCount,
            options.Options1 == RuntimeCharacterOptionsState.DefaultOptions1
                && options.Options2
                    == RuntimeCharacterOptionsState.DefaultOptions2,
            MovementSkills.RunSkill == -1
                && MovementSkills.JumpSkill == -1
                && MovementSkills.Burden == 0f
                && MovementSkills.CurrentStamina == -1
                && MovementSkills.OwnPwdBitfield == 0u
                && MovementSkills.PlayerKillerStatus == -1
                && MovementSkills.LastPkAttackTimestamp is null
                && _runSkillBase == -1
                && _jumpSkillBase == -1
                && _movementSkillAugmentations == default,
            AutonomyLevel == FullAutonomyLevel,
            OptionsAreClean: !Options.IsDirty,
            TitleCount: Titles.Count,
            DisplayTitleIsDefault: Titles.DisplayTitleId == 0u);
    }

    public void UpdateMovementSkillBase(int runSkillBase, int jumpSkillBase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (runSkillBase >= 0) _runSkillBase = runSkillBase;
        if (jumpSkillBase >= 0) _jumpSkillBase = jumpSkillBase;
        RecomputeMovementSkills();
    }

    public void UpdateMovementSkillAugmentations(
        PlayerSkillMath.AugmentationBonuses augmentations)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_movementSkillAugmentations == augmentations)
            return;
        _movementSkillAugmentations = augmentations;
        RecomputeMovementSkills();
    }

    private void RecomputeMovementSkills()
    {
        int run = _runSkillBase >= 0
            ? CalculateMovementSkill(_runSkillBase, RunSkillId)
            : -1;
        int jump = _jumpSkillBase >= 0
            ? CalculateMovementSkill(_jumpSkillBase, JumpSkillId)
            : -1;
        EnchantmentMath.VitalMod runMod = Spellbook.GetSkillMod(RunSkillId);
        int activeCount = System.Linq.Enumerable.Count(Spellbook.ActiveEnchantments);
        System.Console.WriteLine(
            System.FormattableString.Invariant(
                $"[stat-chain] base run={_runSkillBase} jump={_jumpSkillBase} runMod={runMod.Multiplier:F4}x+{runMod.Additive:F1} -> eff run={run} jump={jump} (activeEnchantments={activeCount})"));
        MovementSkills.Update(run, jump);
    }

    private int CalculateMovementSkill(int baseSkill, uint skillId)
    {
        EnchantmentMath.VitalMod mod = Spellbook.GetSkillMod(skillId);
        float vitae = EnchantmentMath.GetVitaeMultiplier(
            Spellbook.ActiveEnchantments);
        uint advancementClass = LocalPlayer.GetSkill(skillId)?.Status ?? 0u;
        int enchantedBaseSkill =
            baseSkill + LocalPlayer.AttributeEnchantmentSkillDelta(skillId);
        return PlayerSkillMath.Calculate(
            baseSkill,
            enchantedBaseSkill,
            skillId,
            advancementClass,
            _movementSkillAugmentations,
            mod,
            vitae).EffectiveLevel;
    }

    private void OnEnchantmentsChangedForMovement() => RecomputeMovementSkills();

    public void InstallSpellMetadata(SpellTable spellTable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Spellbook.InstallMetadata(spellTable);
    }

    public void ResetSpellbook()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Spellbook.Clear();
    }

    public void ResetLocalPlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LocalPlayer.Clear();
    }

    public bool TryAddFavorite(
        int tabIndex,
        int position,
        uint spellId,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if ((uint)tabIndex >= 8u || position < 0 || spellId == 0u)
            return false;
        Spellbook.SetFavorite(tabIndex, position, spellId);
        publishOutbound();
        return true;
    }

    public bool TryRemoveFavorite(
        int tabIndex,
        uint spellId,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if ((uint)tabIndex >= 8u || spellId == 0u)
            return false;
        Spellbook.RemoveFavorite(tabIndex, spellId);
        publishOutbound();
        return true;
    }

    public void SetSpellbookFilter(
        uint filters,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if (Spellbook.SpellbookFilters == filters)
            return;
        Spellbook.SetSpellbookFilters(filters);
        publishOutbound();
    }

    public bool TrySetDesiredComponent(
        uint componentId,
        uint amount,
        Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        if (componentId == 0u || amount > 5000u)
            return false;
        try
        {
            publishOutbound();
        }
        finally
        {
            Spellbook.SetDesiredComponent(componentId, amount);
        }
        return true;
    }

    public void ClearDesiredComponents(Action publishOutbound)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(publishOutbound);
        try
        {
            publishOutbound();
        }
        finally
        {
            Spellbook.ClearDesiredComponents();
        }
    }

    public void ResetSession()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        List<Exception>? failures = null;
        Try(Spellbook.Clear, ref failures);
        Try(LocalPlayer.Clear, ref failures);
        Try(Options.ResetSession, ref failures);
        _runSkillBase = -1;
        _jumpSkillBase = -1;
        _movementSkillAugmentations = default;
        Volatile.Write(ref _autonomyLevel, FullAutonomyLevel);
        Try(MovementSkills.ResetSession, ref failures);
        Try(Titles.ResetSession, ref failures);
        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime character state did not converge during reset.",
                failures);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        try
        {
            Try(Spellbook.Clear, ref failures);
            Try(LocalPlayer.Clear, ref failures);
            Try(Options.ResetSession, ref failures);
            _runSkillBase = -1;
            _jumpSkillBase = -1;
            _movementSkillAugmentations = default;
            Volatile.Write(ref _autonomyLevel, FullAutonomyLevel);
            Try(MovementSkills.ResetSession, ref failures);
            Try(Titles.ResetSession, ref failures);
        }
        finally
        {
            Spellbook.StateChanged -= OnSpellbookChanged;
            Spellbook.EnchantmentsChanged -= OnEnchantmentsChangedForMovement;
            LocalPlayer.Changed -= OnVitalChanged;
            LocalPlayer.AttributeChanged -= OnAttributeChanged;
            LocalPlayer.CharacterChanged -= OnCharacterChanged;
            _internalSubscriptionsAttached = false;
            _disposed = true;
        }
        if (failures is not null)
        {
            throw new AggregateException(
                "Runtime character state did not converge during disposal.",
                failures);
        }
    }

    private void OnSpellbookChanged() =>
        Interlocked.Increment(ref _spellbookRevision);

    private void OnVitalChanged(LocalPlayerState.VitalKind _) =>
        Interlocked.Increment(ref _characterRevision);

    private void OnAttributeChanged(LocalPlayerState.AttributeKind _) =>
        Interlocked.Increment(ref _characterRevision);

    private void OnCharacterChanged() =>
        Interlocked.Increment(ref _characterRevision);

    private static void Try(Action action, ref List<Exception>? failures)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            (failures ??= []).Add(error);
        }
    }

    private sealed class CharacterView(RuntimeCharacterState owner)
        : IRuntimeCharacterView
    {
        public RuntimeCharacterSnapshot Snapshot => new(
            Interlocked.Read(ref owner._characterRevision),
            Interlocked.Read(ref owner._spellbookRevision),
            owner.Options.Snapshot,
            owner.MovementSkills.Snapshot,
            owner.Spellbook.LearnedSpells.Count,
            owner.Spellbook.ActiveEnchantments.Count(),
            owner.Spellbook.DesiredComponents.Count,
            owner.LocalPlayer.Skills.Count,
            owner.Spellbook.SpellbookFilters,
            owner.Titles.Snapshot);

        public bool TryGetVital(int kind, out RuntimeVitalSnapshot vital)
        {
            if (!Enum.IsDefined((LocalPlayerState.VitalKind)kind)
                || owner.LocalPlayer.Get((LocalPlayerState.VitalKind)kind)
                    is not LocalPlayerState.VitalSnapshot current)
            {
                vital = default;
                return false;
            }

            vital = new RuntimeVitalSnapshot(
                kind,
                current.Ranks,
                current.Start,
                current.Xp,
                current.Current,
                owner.LocalPlayer.GetMaxApprox(
                    (LocalPlayerState.VitalKind)kind) ?? 0u);
            return true;
        }

        public bool TryGetAttribute(
            int kind,
            out RuntimeAttributeSnapshot attribute)
        {
            if (!Enum.IsDefined((LocalPlayerState.AttributeKind)kind)
                || owner.LocalPlayer.GetAttribute(
                    (LocalPlayerState.AttributeKind)kind)
                    is not LocalPlayerState.AttributeSnapshot current)
            {
                attribute = default;
                return false;
            }

            attribute = new RuntimeAttributeSnapshot(
                kind,
                current.Ranks,
                current.Start,
                current.Xp,
                current.Current);
            return true;
        }

        public bool TryGetSkill(uint skillId, out RuntimeSkillSnapshot skill)
        {
            if (owner.LocalPlayer.GetSkill(skillId)
                is not LocalPlayerState.SkillSnapshot current)
            {
                skill = default;
                return false;
            }

            skill = new RuntimeSkillSnapshot(
                current.SkillId,
                current.Ranks,
                current.Status,
                current.Xp,
                current.Init,
                current.Resistance,
                current.LastUsed,
                current.FormulaBonus,
                current.CurrentLevel);
            return true;
        }

        public bool KnowsSpell(uint spellId) =>
            owner.Spellbook.Knows(spellId);

        public bool TryGetFavorite(
            int tabIndex,
            int position,
            out uint spellId)
        {
            IReadOnlyList<uint> favorites =
                owner.Spellbook.GetFavorites(tabIndex);
            if ((uint)position >= (uint)favorites.Count)
            {
                spellId = 0u;
                return false;
            }
            spellId = favorites[position];
            return true;
        }

        public bool TryGetDesiredComponent(
            uint componentId,
            out uint amount) =>
            owner.Spellbook.DesiredComponents.TryGetValue(
                componentId,
                out amount);
    }
}

public readonly record struct RuntimeCharacterOptionsSnapshot(
    uint Options1,
    uint Options2,
    long Revision)
{
    public bool DragItemOnPlayerOpensSecureTrade =>
        (Options1
            & (uint)PlayerDescriptionParser.CharacterOptions1
                .DragItemOnPlayerOpensSecureTrade) != 0u;
}

public sealed class RuntimeCharacterOptionsState
{
    public const uint DefaultOptions1 =
        (uint)PlayerDescriptionParser.CharacterOptions1.Default;
    public const uint DefaultOptions2 = 0x00948700u;

    public static TimeSpan AutoSaveDelay => TimeSpan.FromSeconds(480);

    private readonly TimeProvider _timeProvider;
    private readonly object _dirtyGate = new();

    private long _dirtyGeneration;
    private uint _options1 = DefaultOptions1;
    private uint _options2 = DefaultOptions2;
    private long _revision;
    private bool _isDirty;
    private DateTimeOffset _firstDirtiedAt;
    private bool _hasServerSeed;

    public RuntimeCharacterOptionsState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public uint Options1 => Volatile.Read(ref _options1);
    public uint Options2 => Volatile.Read(ref _options2);
    public long Revision => Interlocked.Read(ref _revision);
    public RuntimeCharacterOptionsSnapshot Snapshot =>
        new(_options1, _options2, Revision);

    public bool IsDirty
    {
        get { lock (_dirtyGate) return _isDirty; }
    }

    public DateTimeOffset? FirstDirtiedAt
    {
        get { lock (_dirtyGate) return _isDirty ? _firstDirtiedAt : null; }
    }

    public bool HasServerSeed
    {
        get { lock (_dirtyGate) return _hasServerSeed; }
    }

    public bool DragItemOnPlayerOpensSecureTrade =>
        Snapshot.DragItemOnPlayerOpensSecureTrade;

    public void Replace(uint options1, uint options2, bool armServerSeed = true)
    {
        Volatile.Write(ref _options1, options1);
        Volatile.Write(ref _options2, options2);
        Interlocked.Increment(ref _revision);
        lock (_dirtyGate)
        {
            _isDirty = false;
            if (armServerSeed)
                _hasServerSeed = true;
        }
    }

    public bool TrySetOption(uint characterOptionId, bool value, Action<uint, bool> sendAutoSave)
    {
        ArgumentNullException.ThrowIfNull(sendAutoSave);
        if (!CharacterOptionTable.TryGet(characterOptionId, out CharacterOptionTableEntry entry))
            return false;

        uint word = entry.IsOptions1 ? Options1 : Options2;
        if (((word & entry.Mask) != 0u) == value)
            return true;

        SetOptionBit(characterOptionId, value);

        if (value && characterOptionId == (uint)CharacterOptionId.IgnoreFellowshipRequests)
        {
            TrySetOption(
                (uint)CharacterOptionId.FellowshipAutoAcceptRequests,
                false,
                sendAutoSave);
        }
        else if (value && characterOptionId == (uint)CharacterOptionId.FellowshipAutoAcceptRequests)
        {
            TrySetOption(
                (uint)CharacterOptionId.IgnoreFellowshipRequests,
                false,
                sendAutoSave);
        }

        if (entry.IsAutoSave)
            sendAutoSave(characterOptionId, value);
        else
            MarkDirty();

        return true;
    }

    public bool GetOptionBit(uint characterOptionId)
    {
        if (!CharacterOptionTable.TryGet(characterOptionId, out CharacterOptionTableEntry entry))
            return false;

        uint word = entry.IsOptions1 ? Options1 : Options2;
        return (word & entry.Mask) != 0u;
    }

    public bool GetOptionBit(CharacterOptionId characterOptionId) =>
        GetOptionBit((uint)characterOptionId);

    public void SetOptionBit(uint characterOptionId, bool value)
    {
        if (!CharacterOptionTable.TryGet(characterOptionId, out CharacterOptionTableEntry entry))
            return;

        if (entry.IsOptions1)
        {
            uint updated = value ? (Options1 | entry.Mask) : (Options1 & ~entry.Mask);
            Volatile.Write(ref _options1, updated);
        }
        else
        {
            uint updated = value ? (Options2 | entry.Mask) : (Options2 & ~entry.Mask);
            Volatile.Write(ref _options2, updated);
        }

        Interlocked.Increment(ref _revision);
    }

    public void MarkDirty()
    {
        lock (_dirtyGate)
        {
            _dirtyGeneration++;
            if (_isDirty) return;
            _isDirty = true;
            _firstDirtiedAt = _timeProvider.GetUtcNow();
        }
    }

    public bool TryFlush(Action flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        long observedGeneration;
        lock (_dirtyGate)
        {
            if (!_isDirty || !_hasServerSeed) return false;
            observedGeneration = _dirtyGeneration;
        }
        flush();
        lock (_dirtyGate)
        {
            if (_dirtyGeneration == observedGeneration)
                _isDirty = false;
        }
        return true;
    }

    public bool TryFlushIfAutoSaveDue(Action flush)
    {
        ArgumentNullException.ThrowIfNull(flush);
        long observedGeneration;
        lock (_dirtyGate)
        {
            if (!_isDirty || !_hasServerSeed) return false;
            if (_timeProvider.GetUtcNow() - _firstDirtiedAt < AutoSaveDelay) return false;
            observedGeneration = _dirtyGeneration;
        }
        flush();
        lock (_dirtyGate)
        {
            if (_dirtyGeneration == observedGeneration)
                _isDirty = false;
        }
        return true;
    }

    public void ResetSession()
    {
        Volatile.Write(ref _options1, DefaultOptions1);
        Volatile.Write(ref _options2, DefaultOptions2);
        Interlocked.Increment(ref _revision);
        lock (_dirtyGate)
        {
            _isDirty = false;
            _hasServerSeed = false;
        }
    }
}

public readonly record struct RuntimeMovementSkillSnapshot(
    int RunSkill,
    int JumpSkill,
    long Revision,
    float Burden = 0f,
    int CurrentStamina = -1,
    uint OwnPwdBitfield = 0u,
    int PlayerKillerStatus = -1,
    float? LastPkAttackTimestamp = null)
{
    public bool IsComplete => RunSkill >= 0 && JumpSkill >= 0;
}

public sealed class RuntimeMovementSkillState
{
    private int _runSkill = -1;
    private int _jumpSkill = -1;
    private float _burden;
    private int _currentStamina = -1;
    private uint _ownPwdBitfield;
    private int _playerKillerStatus = -1;
    private float _lastPkAttackTimestamp;
    private bool _hasLastPkAttackTimestamp;
    private long _revision;

    public int RunSkill => Volatile.Read(ref _runSkill);
    public int JumpSkill => Volatile.Read(ref _jumpSkill);
    public float Burden => Volatile.Read(ref _burden);
    public int CurrentStamina => Volatile.Read(ref _currentStamina);
    public uint OwnPwdBitfield => Volatile.Read(ref _ownPwdBitfield);
    public int PlayerKillerStatus => Volatile.Read(ref _playerKillerStatus);
    public float? LastPkAttackTimestamp =>
        Volatile.Read(ref _hasLastPkAttackTimestamp)
            ? Volatile.Read(ref _lastPkAttackTimestamp)
            : null;
    public bool IsComplete => _runSkill >= 0 && _jumpSkill >= 0;
    public long Revision => Interlocked.Read(ref _revision);
    public RuntimeMovementSkillSnapshot Snapshot =>
        new(
            _runSkill,
            _jumpSkill,
            Revision,
            _burden,
            _currentStamina,
            OwnPwdBitfield,
            PlayerKillerStatus,
            LastPkAttackTimestamp);

    public void Update(int runSkill, int jumpSkill)
    {
        bool changed = false;
        if (runSkill >= 0 && RunSkill != runSkill)
        {
            Volatile.Write(ref _runSkill, runSkill);
            changed = true;
        }
        if (jumpSkill >= 0 && JumpSkill != jumpSkill)
        {
            Volatile.Write(ref _jumpSkill, jumpSkill);
            changed = true;
        }
        if (changed)
            Interlocked.Increment(ref _revision);
    }

    public void UpdateBurden(float burden)
    {
        if (Burden == burden) return;
        Volatile.Write(ref _burden, burden);
        Interlocked.Increment(ref _revision);
    }

    public void UpdateStamina(int currentStamina)
    {
        if (CurrentStamina == currentStamina) return;
        Volatile.Write(ref _currentStamina, currentStamina);
        Interlocked.Increment(ref _revision);
    }

    public void UpdateOwnPwdBitfield(uint bitfield)
    {
        if (OwnPwdBitfield == bitfield) return;
        Volatile.Write(ref _ownPwdBitfield, bitfield);
        Interlocked.Increment(ref _revision);
    }

    public void UpdatePlayerKillerStatus(int playerKillerStatus, float? lastPkAttackTimestamp)
    {
        bool changed = false;
        if (PlayerKillerStatus != playerKillerStatus)
        {
            Volatile.Write(ref _playerKillerStatus, playerKillerStatus);
            changed = true;
        }
        bool hasTimestamp = lastPkAttackTimestamp.HasValue;
        float timestamp = lastPkAttackTimestamp ?? 0f;
        if (Volatile.Read(ref _hasLastPkAttackTimestamp) != hasTimestamp
            || (hasTimestamp && Volatile.Read(ref _lastPkAttackTimestamp) != timestamp))
        {
            Volatile.Write(ref _lastPkAttackTimestamp, timestamp);
            Volatile.Write(ref _hasLastPkAttackTimestamp, hasTimestamp);
            changed = true;
        }
        if (changed)
            Interlocked.Increment(ref _revision);
    }

    public void ResetSession()
    {
        Volatile.Write(ref _runSkill, -1);
        Volatile.Write(ref _jumpSkill, -1);
        Volatile.Write(ref _burden, 0f);
        Volatile.Write(ref _currentStamina, -1);
        Volatile.Write(ref _ownPwdBitfield, 0u);
        Volatile.Write(ref _playerKillerStatus, -1);
        Volatile.Write(ref _lastPkAttackTimestamp, 0f);
        Volatile.Write(ref _hasLastPkAttackTimestamp, false);
        Interlocked.Increment(ref _revision);
    }
}

public readonly record struct RuntimeCharacterTitleSnapshot(
    uint DisplayTitleId,
    int TitleCount,
    long Revision);

public sealed class RuntimeCharacterTitleState
{
    private readonly object _gate = new();
    private readonly HashSet<uint> _earnedTitleIds = new();
    private uint _displayTitleId;
    private long _revision;

    public event Action? TableReplaced;

    public event Action<uint>? TitleAdded;

    public event Action<uint>? DisplayTitleChanged;

    public uint DisplayTitleId => Volatile.Read(ref _displayTitleId);
    public long Revision => Interlocked.Read(ref _revision);

    public IReadOnlyCollection<uint> EarnedTitleIds
    {
        get { lock (_gate) return _earnedTitleIds.ToArray(); }
    }

    public int Count
    {
        get { lock (_gate) return _earnedTitleIds.Count; }
    }

    public bool HasEarnedTitle(uint titleId)
    {
        lock (_gate) return _earnedTitleIds.Contains(titleId);
    }

    public RuntimeCharacterTitleSnapshot Snapshot
    {
        get
        {
            int count;
            lock (_gate) count = _earnedTitleIds.Count;
            return new RuntimeCharacterTitleSnapshot(DisplayTitleId, count, Revision);
        }
    }

    public void ReplaceTable(uint displayTitleId, IReadOnlyList<uint> titleIds)
    {
        ArgumentNullException.ThrowIfNull(titleIds);
        bool setChanged;
        bool displayChanged;
        lock (_gate)
        {
            setChanged = !_earnedTitleIds.SetEquals(titleIds);
            _earnedTitleIds.Clear();
            foreach (uint id in titleIds)
                _earnedTitleIds.Add(id);
            displayChanged = _displayTitleId != displayTitleId;
            if (displayChanged)
                Volatile.Write(ref _displayTitleId, displayTitleId);
        }
        if (setChanged || displayChanged)
            Interlocked.Increment(ref _revision);
        TableReplaced?.Invoke();
        if (displayChanged)
            DisplayTitleChanged?.Invoke(displayTitleId);
    }

    public void ApplyUpdateTitle(uint titleId, bool setAsDisplay)
    {
        bool added;
        bool displayChanged;
        lock (_gate)
        {
            added = _earnedTitleIds.Add(titleId);
            displayChanged = setAsDisplay && _displayTitleId != titleId;
            if (displayChanged)
                Volatile.Write(ref _displayTitleId, titleId);
        }
        if (added)
            Interlocked.Increment(ref _revision);
        if (displayChanged)
            Interlocked.Increment(ref _revision);
        if (added)
            TitleAdded?.Invoke(titleId);
        if (displayChanged)
            DisplayTitleChanged?.Invoke(titleId);
    }

    public void ResetSession()
    {
        uint previousDisplayTitleId;
        lock (_gate)
        {
            previousDisplayTitleId = _displayTitleId;
            _earnedTitleIds.Clear();
            Volatile.Write(ref _displayTitleId, 0u);
        }
        Interlocked.Increment(ref _revision);
        TableReplaced?.Invoke();
        if (previousDisplayTitleId != 0u)
            DisplayTitleChanged?.Invoke(0u);
    }
}
