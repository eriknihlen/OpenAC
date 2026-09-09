using AcDream.Core.CharGen;
using AcDream.Core.Net.Messages;

namespace AcDream.Runtime.Session;

public enum ChargenAttributeId
{
    Strength = 1,
    Endurance = 2,
    Quickness = 3,
    Coordination = 4,
    Focus = 5,
    Self = 6,
}

public enum ChargenAppearanceSlot
{
    EyesStrip,
    NoseStrip,
    MouthStrip,
    HairStyle,
    HairColor,
    EyeColor,
    HeadgearStyle,
    HeadgearColor,
    ShirtStyle,
    ShirtColor,
    TrousersStyle,
    TrousersColor,
    FootwearStyle,
    FootwearColor,
}

public enum ChargenShadeSlot
{
    Skin,
    Hair,
    Headgear,
    Shirt,
    Trousers,
    Footwear,
}

public enum RuntimeCharacterCreationDeltaKind
{
    Reset,
    StateChanged,
    FinishRefused,
    FinishSent,
    Created,
    CreationFailed,
    RejectionAcknowledged,
}

public readonly record struct RuntimeCharacterCreationAppearance(
    uint EyesStrip,
    uint NoseStrip,
    uint MouthStrip,
    uint HairStyle,
    uint HairColor,
    uint EyeColor,
    uint HeadgearStyle,
    uint HeadgearColor,
    uint ShirtStyle,
    uint ShirtColor,
    uint TrousersStyle,
    uint TrousersColor,
    uint FootwearStyle,
    uint FootwearColor,
    double SkinShade,
    double HairShade,
    double HeadgearShade,
    double ShirtShade,
    double TrousersShade,
    double FootwearShade)
{
    public const uint Unset = 0xFFFFFFFFu;

    public const double UnsetShade = -1.0;

    public static RuntimeCharacterCreationAppearance Default { get; } = new(
        Unset, Unset, Unset,
        Unset, Unset, Unset,
        Unset, Unset,
        Unset, Unset,
        Unset, Unset,
        Unset, Unset,
        UnsetShade, UnsetShade, UnsetShade,
        UnsetShade, UnsetShade, UnsetShade);
}

public readonly record struct RuntimeCharacterCreationLocalRefusal(
    bool NoName,
    bool AttributeCreditsUnspent,
    bool AlreadyPending,
    bool RosterFull,
    bool HeritageOrGenderUnset = false)
{
    public bool Any =>
        NoName || AttributeCreditsUnspent || AlreadyPending || RosterFull
        || HeritageOrGenderUnset;

    public static RuntimeCharacterCreationLocalRefusal None { get; } = default;
}

public readonly record struct RuntimeCharacterCreationIdentity(
    uint Guid,
    string Name);

public readonly record struct RuntimeCharacterCreationRejection(
    uint RawCode,
    CharGenVerificationResponse.Code Code,
    string Reason,
    string AttemptedName);

public readonly record struct RuntimeCharacterCreationSnapshot(
    RuntimeGenerationToken Generation,
    bool IsActive,
    long Revision,
    uint HeritageId,
    uint GenderKey,
    RuntimeCharacterCreationAppearance Appearance,
    uint Template,
    ChargenAttributeValues Attributes,
    uint AttributeLockMask,
    uint TotalAttributeCredits,
    int RemainingAttributeCredits,
    uint TotalSkillCredits,
    int RemainingSkillCredits,
    string Name,
    int StartArea,
    uint Slot,
    bool VerificationPending,
    RuntimeCharacterCreationLocalRefusal LastLocalRefusal,
    RuntimeCharacterCreationRejection? LastRejection,
    RuntimeCharacterCreationIdentity? LastCreated)
{
    public const uint TemplateUnset = 0xFFFFFFFFu;

    public bool IsAttributeLocked(ChargenAttributeId attributeId) =>
        (AttributeLockMask & (1u << ((int)attributeId - 1))) != 0u;
}

public readonly record struct RuntimeCharacterCreationDelta(
    RuntimeGenerationToken Generation,
    ulong Sequence,
    long Revision,
    RuntimeCharacterCreationDeltaKind Kind);

public interface IRuntimeCharacterCreationObserver
{
    void OnCharacterCreationChanged(in RuntimeCharacterCreationDelta delta);
}

public interface IRuntimeCharacterCreationEventSource
{
    IDisposable Subscribe(IRuntimeCharacterCreationObserver observer);
}

public interface IRuntimeCharacterCreationView : IRuntimeCharacterCreationEventSource
{
    RuntimeCharacterCreationSnapshot Snapshot { get; }

    ChargenSkillAdvancementClass GetSkillLevel(uint skillId);

    ChargenOptions Options { get; }
}

public sealed class RuntimeCharacterCreationState : IDisposable
{
    private static readonly ChargenAttributeId[] BalanceOrder =
    [
        ChargenAttributeId.Strength,
        ChargenAttributeId.Endurance,
        ChargenAttributeId.Coordination,
        ChargenAttributeId.Quickness,
        ChargenAttributeId.Focus,
        ChargenAttributeId.Self,
    ];

    private sealed class ViewProjection(RuntimeCharacterCreationState owner)
        : IRuntimeCharacterCreationView
    {
        public RuntimeCharacterCreationSnapshot Snapshot => owner.Snapshot;

        public ChargenSkillAdvancementClass GetSkillLevel(uint skillId) =>
            owner.GetSkillLevel(skillId);

        public ChargenOptions Options => owner._options;

        public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer) =>
            owner._events.Subscribe(observer);
    }

    private readonly object _gate = new();
    private readonly CharacterCreationEventStream _events = new();
    private readonly ViewProjection _view;
    private ChargenOptions _options;
    private readonly Random _random;
    private RuntimeGenerationToken _generation;
    private bool _active;
    private long _revision;
    private uint _heritageId;
    private uint _genderKey;
    private RuntimeCharacterCreationAppearance _appearance =
        RuntimeCharacterCreationAppearance.Default;
    private uint _template = RuntimeCharacterCreationSnapshot.TemplateUnset;
    private ChargenAttributeValues _attributes;
    private uint _attributeLockMask;
    private uint _totalAttributeCredits;
    private int _remainingAttributeCredits;
    private uint _totalSkillCredits;
    private int _remainingSkillCredits;
    private readonly ChargenSkillAdvancementSet _skills = new();
    private int _attributeBalanceCursor = 1;
    private string _name = string.Empty;
    private int _startArea = -1;
    private uint _slot;
    private bool _verificationPending;
    private RuntimeCharacterCreationLocalRefusal _lastLocalRefusal;
    private RuntimeCharacterCreationRejection? _lastRejection;
    private RuntimeCharacterCreationIdentity? _lastCreated;
    private bool _disposed;

    public RuntimeCharacterCreationState(
        ChargenOptions options,
        Random? random = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _random = random ?? Random.Shared;
        _view = new ViewProjection(this);
    }

    public IRuntimeCharacterCreationView View => _view;

    public ChargenOptions Options => _options;

    public void InstallOptions(ChargenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_active)
            {
                throw new InvalidOperationException(
                    "Chargen options cannot be installed while a character-creation session is active.");
            }
            _options = options;
        }
    }

    public RuntimeCharacterCreationSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new RuntimeCharacterCreationSnapshot(
                    _generation,
                    _active,
                    _revision,
                    _heritageId,
                    _genderKey,
                    _appearance,
                    _template,
                    _attributes,
                    _attributeLockMask,
                    _totalAttributeCredits,
                    _remainingAttributeCredits,
                    _totalSkillCredits,
                    _remainingSkillCredits,
                    _name,
                    _startArea,
                    _slot,
                    _verificationPending,
                    _lastLocalRefusal,
                    _lastRejection,
                    _lastCreated);
            }
        }
    }

    // ── Lifecycle ───────────────────────────────────────────────────────

    internal void Begin(RuntimeGenerationToken generation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _generation = generation;
            _active = true;
            ClearSessionState();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.Reset);
    }

    internal void CompleteEnter()
    {
        lock (_gate)
        {
            if (_disposed || !_active)
                return;
            _active = false;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
    }

    internal void Reset(RuntimeGenerationToken generation)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _generation = generation;
            _active = false;
            ClearSessionState();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.Reset);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _active = false;
            ClearSessionState();
            _revision++;
        }
        _events.Dispose();
    }

    // ── Heritage / gender / template ───────────────────────────────────

    internal bool TrySelectHeritage(uint heritageId)
    {
        if (!_options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage))
            return false;

        lock (_gate)
        {
            if (_disposed || !_active)
                return false;

            SetHeritageGroupLocked(heritageId, heritage);
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    private void SetHeritageGroupLocked(uint heritageId, ChargenHeritageOptions heritage)
    {
        _heritageId = heritageId;
        _totalAttributeCredits = heritage.AttributeCredits;
        _totalSkillCredits = heritage.SkillCredits;
        _remainingSkillCredits = checked((int)heritage.SkillCredits);

        ApplyTemplateLocked(heritage);
        RandomizeStartAreaLocked(heritage);
        ConstrainAppearanceByGenderLocked();
        RecomputeRemainingAttributeCreditsLocked();
        if (RecomputeSkillSpendLocked(heritage) < 0)
            ResetSkillLevelsLocked(heritage);
    }

    internal bool TrySelectGender(uint genderKey)
    {
        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0)
                return false;
            if (!_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? heritage)
                || !heritage.GendersByKey.ContainsKey((int)genderKey))
            {
                return false;
            }

            SetGenderLocked(genderKey);
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    private void SetGenderLocked(uint genderKey)
    {
        _genderKey = genderKey;
        ConstrainAppearanceByGenderLocked();
    }

    internal bool TrySelectTemplate(uint templateIndex)
    {
        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0 || _genderKey == 0)
                return false;
            if (!_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? heritage)
                || templateIndex >= (uint)heritage.Templates.Count)
            {
                return false;
            }

            _template = templateIndex;
            ApplyTemplateLocked(heritage);
            RecomputeRemainingAttributeCreditsLocked();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    private void ApplyTemplateLocked(ChargenHeritageOptions heritage)
    {
        _attributeLockMask = 0u;
        if (_heritageId == (uint)ChargenHeritageGroup.Olthoi
            || _heritageId == (uint)ChargenHeritageGroup.OlthoiAcid)
        {
            _template = 0u;
        }

        if (_heritageId == 0 || _genderKey == 0 || _template == RuntimeCharacterCreationSnapshot.TemplateUnset)
            return;
        if (_template >= (uint)heritage.Templates.Count)
        {
            _template = RuntimeCharacterCreationSnapshot.TemplateUnset;
            return;
        }

        ChargenTemplate row = heritage.Templates[(int)_template];
        _attributes = row.Attributes;

        ResetSkillLevelsLocked(heritage);
        foreach (uint skillId in row.NormalSkills)
            ApplyTemplateSkillEntryLocked(heritage, skillId, ChargenSkillAdvancementClass.Trained);
        foreach (uint skillId in row.PrimarySkills)
            ApplyTemplateSkillEntryLocked(heritage, skillId, ChargenSkillAdvancementClass.Specialized);
    }

    private void ApplyTemplateSkillEntryLocked(
        ChargenHeritageOptions heritage,
        uint skillId,
        ChargenSkillAdvancementClass targetClass)
    {
        if (skillId == 0 || skillId >= ChargenSkillAdvancementSet.SlotCount)
            return;

        ChargenSkillAdvancementClass previous = _skills[skillId];
        int remaining = _remainingSkillCredits;
        if (previous == ChargenSkillAdvancementClass.Trained
            && TryGetSkillCost(heritage, skillId, out int prevTrained, out _))
        {
            remaining += prevTrained;
        }
        else if (previous == ChargenSkillAdvancementClass.Specialized
            && TryGetSkillCost(heritage, skillId, out _, out int prevSpecialized))
        {
            remaining += prevSpecialized;
        }

        if (!TryGetSkillCost(heritage, skillId, out int trainedCost, out int specializedCost))
            return;

        int charge = targetClass switch
        {
            ChargenSkillAdvancementClass.Specialized => specializedCost,
            ChargenSkillAdvancementClass.Trained => trainedCost,
            _ => 0,
        };
        remaining -= charge;
        if (remaining < 0)
            return;

        _skills[skillId] = targetClass;
        _remainingSkillCredits = remaining;
    }

    private void ResetSkillLevelsLocked(ChargenHeritageOptions heritage)
    {
        _remainingSkillCredits = checked((int)_totalSkillCredits);
        if (_heritageId == 0 || _genderKey == 0)
            return;

        for (uint skillId = 1; skillId < ChargenSkillAdvancementSet.SlotCount; skillId++)
        {
            if (!TryGetSkillCost(heritage, skillId, out int trainedCost, out int specializedCost))
                continue;

            _skills[skillId] = trainedCost > 0
                ? ChargenSkillAdvancementClass.Untrained
                : specializedCost <= 0
                    ? ChargenSkillAdvancementClass.Specialized
                    : ChargenSkillAdvancementClass.Trained;
        }
    }

    private bool TryGetSkillCost(
        ChargenHeritageOptions heritage,
        uint skillId,
        out int trainedCost,
        out int specializedCost)
    {
        if (heritage.SkillCostsBySkillId.TryGetValue(skillId, out ChargenSkillCost cost)
            || _options.GlobalSkillCostsBySkillId.TryGetValue(skillId, out cost))
        {
            trainedCost = cost.NormalCost;
            specializedCost = cost.PrimaryCost;
            return true;
        }
        trainedCost = 0;
        specializedCost = 0;
        return false;
    }

    private int RecomputeSkillSpendLocked(ChargenHeritageOptions heritage) =>
        checked((int)_totalSkillCredits)
        - ChargenSkillCreditMath.ComputeSpent(
            _skills,
            heritage.SkillCostsBySkillId,
            _options.GlobalSkillCostsBySkillId);

    private void RandomizeStartAreaLocked(ChargenHeritageOptions heritage)
    {
        if (heritage.PrimaryStartAreaIndices.Count == 0)
            return;
        int candidate = heritage.PrimaryStartAreaIndices[
            _random.Next(heritage.PrimaryStartAreaIndices.Count)];
        _startArea = candidate >= 0 && candidate < _options.StarterAreas.Count
            ? candidate
            : -1;
    }

    // ── Attributes ──────────────────────────────────────────────────────

    internal bool TrySetAttribute(ChargenAttributeId attributeId, int requestedValue)
    {
        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0)
                return false;

            int current = GetAttributeLocked(attributeId);
            int clamped = Math.Clamp(
                requestedValue,
                ChargenAttributeMath.AttributeMin,
                ChargenAttributeMath.AttributeMax);
            if (clamped > current)
            {
                int absRemaining = GetAbsRemainingCreditsLocked(attributeId);
                if (clamped - current > absRemaining)
                    clamped = current + absRemaining;
                if (clamped < current)
                    clamped = current;
            }

            SetAttributeRawLocked(attributeId, clamped);
            BalanceAttributesLocked(attributeId);
            RecomputeRemainingAttributeCreditsLocked();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TrySetAttributeLock(ChargenAttributeId attributeId, bool locked)
    {
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            uint bit = 1u << ((int)attributeId - 1);
            _attributeLockMask = locked
                ? _attributeLockMask | bit
                : _attributeLockMask & ~bit;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    private int GetAbsRemainingCreditsLocked(ChargenAttributeId queriedAttributeId)
    {
        int total = checked((int)_totalAttributeCredits);
        foreach (ChargenAttributeId id in BalanceOrder)
        {
            bool useCurrent = IsAttributeLockedLocked(id) || id == queriedAttributeId;
            total -= useCurrent
                ? GetAttributeLocked(id)
                : ChargenAttributeMath.AttributeMin;
        }
        return total;
    }

    private void BalanceAttributesLocked(ChargenAttributeId excluded)
    {
        int over = AttributeTotalLocked() - checked((int)_totalAttributeCredits);
        if (over <= 0)
            return;

        bool started = false;
        int guard = 6 * (ChargenAttributeMath.AttributeMax - ChargenAttributeMath.AttributeMin) + 1;
        while (guard-- > 0)
        {
            foreach (ChargenAttributeId id in BalanceOrder)
            {
                if (!started)
                {
                    if ((int)_attributeBalanceCursor != (int)id)
                        continue;
                    started = true;
                }
                if (id == excluded)
                    continue;

                int value = GetAttributeLocked(id);
                if (value > ChargenAttributeMath.AttributeMin && !IsAttributeLockedLocked(id))
                {
                    over -= 1;
                    SetAttributeRawLocked(id, value - 1);
                    if (over <= 0)
                    {
                        int index = Array.IndexOf(BalanceOrder, id);
                        _attributeBalanceCursor = (int)(index == BalanceOrder.Length - 1
                            ? BalanceOrder[0]
                            : BalanceOrder[index + 1]);
                        return;
                    }
                }
            }
            started = true;
        }
    }

    private int AttributeTotalLocked() => _attributes.Total;

    private int GetAttributeLocked(ChargenAttributeId id) => id switch
    {
        ChargenAttributeId.Strength => _attributes.Strength,
        ChargenAttributeId.Endurance => _attributes.Endurance,
        ChargenAttributeId.Quickness => _attributes.Quickness,
        ChargenAttributeId.Coordination => _attributes.Coordination,
        ChargenAttributeId.Focus => _attributes.Focus,
        ChargenAttributeId.Self => _attributes.Self,
        _ => 0,
    };

    private void SetAttributeRawLocked(ChargenAttributeId id, int value)
    {
        _attributes = id switch
        {
            ChargenAttributeId.Strength => _attributes with { Strength = value },
            ChargenAttributeId.Endurance => _attributes with { Endurance = value },
            ChargenAttributeId.Quickness => _attributes with { Quickness = value },
            ChargenAttributeId.Coordination => _attributes with { Coordination = value },
            ChargenAttributeId.Focus => _attributes with { Focus = value },
            ChargenAttributeId.Self => _attributes with { Self = value },
            _ => _attributes,
        };
    }

    private bool IsAttributeLockedLocked(ChargenAttributeId id) =>
        (_attributeLockMask & (1u << ((int)id - 1))) != 0u;

    private void RecomputeRemainingAttributeCreditsLocked() =>
        _remainingAttributeCredits =
            checked((int)_totalAttributeCredits) - AttributeTotalLocked();

    // ── Skills ──────────────────────────────────────────────────────────

    public ChargenSkillAdvancementClass GetSkillLevel(uint skillId)
    {
        lock (_gate)
            return _skills[skillId];
    }

    internal bool TryTrainSkill(uint skillId) =>
        TrySetSkillLevel(skillId, ChargenSkillAdvancementClass.Trained);

    internal bool TrySpecializeSkill(uint skillId) =>
        TrySetSkillLevel(skillId, ChargenSkillAdvancementClass.Specialized);

    internal bool TryUntrainSkill(uint skillId) =>
        TrySetSkillLevel(skillId, ChargenSkillAdvancementClass.Untrained);

    private bool TrySetSkillLevel(uint skillId, ChargenSkillAdvancementClass targetClass)
    {
        if (skillId == 0 || skillId >= ChargenSkillAdvancementSet.SlotCount)
            return false;

        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0 || _genderKey == 0)
                return false;
            if (!_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? heritage))
                return false;
            if (!TryGetSkillCost(heritage, skillId, out int trainedCost, out int specializedCost))
                return false;

            ChargenSkillAdvancementClass previous = _skills[skillId];
            if (previous == targetClass)
                return true;

            int remaining = _remainingSkillCredits;
            remaining += previous switch
            {
                ChargenSkillAdvancementClass.Trained => trainedCost,
                ChargenSkillAdvancementClass.Specialized => specializedCost,
                _ => 0,
            };
            remaining -= targetClass switch
            {
                ChargenSkillAdvancementClass.Trained => trainedCost,
                ChargenSkillAdvancementClass.Specialized => specializedCost,
                _ => 0,
            };
            if (remaining < 0)
                return false;

            _skills[skillId] = targetClass;
            _remainingSkillCredits = remaining;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    // ── Appearance ──────────────────────────────────────────────────────

    internal bool TrySetAppearanceIndex(ChargenAppearanceSlot slot, uint index)
    {
        lock (_gate)
        {
            if (_disposed || !_active
                || !TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            {
                return false;
            }

            if (index != RuntimeCharacterCreationAppearance.Unset)
            {
                int count = AppearanceSlotCountLocked(slot, gender);
                if (index >= (uint)count)
                    return false;
            }

            _appearance = WithAppearanceIndex(_appearance, slot, index);
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TrySetShade(ChargenShadeSlot slot, double value)
    {
        double clamped = Math.Clamp(value, 0.0, 1.0);
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            _appearance = WithShade(_appearance, slot, clamped);
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    private bool TryGetGenderOptionsLocked(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChargenGenderOptions? gender)
    {
        gender = null;
        if (_heritageId == 0 || _genderKey == 0)
            return false;
        if (!_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? heritage))
            return false;
        return heritage.GendersByKey.TryGetValue((int)_genderKey, out gender);
    }

    private static int AppearanceSlotCountLocked(
        ChargenAppearanceSlot slot,
        ChargenGenderOptions gender) => slot switch
    {
        ChargenAppearanceSlot.EyesStrip => gender.EyeStrips.Count,
        ChargenAppearanceSlot.NoseStrip => gender.NoseStrips.Count,
        ChargenAppearanceSlot.MouthStrip => gender.MouthStrips.Count,
        ChargenAppearanceSlot.HairStyle => gender.HairStyles.Count,
        ChargenAppearanceSlot.HairColor => gender.HairColors.Count,
        ChargenAppearanceSlot.EyeColor => gender.EyeColors.Count,
        ChargenAppearanceSlot.HeadgearStyle => gender.Headgears.Count,
        ChargenAppearanceSlot.ShirtStyle => gender.Shirts.Count,
        ChargenAppearanceSlot.TrousersStyle => gender.Pants.Count,
        ChargenAppearanceSlot.FootwearStyle => gender.Footwear.Count,
        ChargenAppearanceSlot.HeadgearColor
            or ChargenAppearanceSlot.ShirtColor
            or ChargenAppearanceSlot.TrousersColor
            or ChargenAppearanceSlot.FootwearColor => gender.ClothingColors.Count,
        _ => 0,
    };

    private static RuntimeCharacterCreationAppearance WithAppearanceIndex(
        RuntimeCharacterCreationAppearance appearance,
        ChargenAppearanceSlot slot,
        uint index) => slot switch
    {
        ChargenAppearanceSlot.EyesStrip => appearance with { EyesStrip = index },
        ChargenAppearanceSlot.NoseStrip => appearance with { NoseStrip = index },
        ChargenAppearanceSlot.MouthStrip => appearance with { MouthStrip = index },
        ChargenAppearanceSlot.HairStyle => appearance with { HairStyle = index },
        ChargenAppearanceSlot.HairColor => appearance with { HairColor = index },
        ChargenAppearanceSlot.EyeColor => appearance with { EyeColor = index },
        ChargenAppearanceSlot.HeadgearStyle => appearance with { HeadgearStyle = index },
        ChargenAppearanceSlot.HeadgearColor => appearance with { HeadgearColor = index },
        ChargenAppearanceSlot.ShirtStyle => appearance with { ShirtStyle = index },
        ChargenAppearanceSlot.ShirtColor => appearance with { ShirtColor = index },
        ChargenAppearanceSlot.TrousersStyle => appearance with { TrousersStyle = index },
        ChargenAppearanceSlot.TrousersColor => appearance with { TrousersColor = index },
        ChargenAppearanceSlot.FootwearStyle => appearance with { FootwearStyle = index },
        ChargenAppearanceSlot.FootwearColor => appearance with { FootwearColor = index },
        _ => appearance,
    };

    private static RuntimeCharacterCreationAppearance WithShade(
        RuntimeCharacterCreationAppearance appearance,
        ChargenShadeSlot slot,
        double value) => slot switch
    {
        ChargenShadeSlot.Skin => appearance with { SkinShade = value },
        ChargenShadeSlot.Hair => appearance with { HairShade = value },
        ChargenShadeSlot.Headgear => appearance with { HeadgearShade = value },
        ChargenShadeSlot.Shirt => appearance with { ShirtShade = value },
        ChargenShadeSlot.Trousers => appearance with { TrousersShade = value },
        ChargenShadeSlot.Footwear => appearance with { FootwearShade = value },
        _ => appearance,
    };

    private void ConstrainAppearanceByGenderLocked()
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
        {
            _appearance = RuntimeCharacterCreationAppearance.Default;
            return;
        }

        RuntimeCharacterCreationAppearance a = _appearance;
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.EyesStrip, ClampIndex(a.EyesStrip, gender.EyeStrips.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.NoseStrip, ClampIndex(a.NoseStrip, gender.NoseStrips.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.MouthStrip, ClampIndex(a.MouthStrip, gender.MouthStrips.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.HairStyle, ClampIndex(a.HairStyle, gender.HairStyles.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.HairColor, ClampIndex(a.HairColor, gender.HairColors.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.EyeColor, ClampIndex(a.EyeColor, gender.EyeColors.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.HeadgearStyle, ClampIndex(a.HeadgearStyle, gender.Headgears.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.HeadgearColor, ClampIndex(a.HeadgearColor, gender.ClothingColors.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.ShirtStyle, ClampIndex(a.ShirtStyle, gender.Shirts.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.ShirtColor, ClampIndex(a.ShirtColor, gender.ClothingColors.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.TrousersStyle, ClampIndex(a.TrousersStyle, gender.Pants.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.TrousersColor, ClampIndex(a.TrousersColor, gender.ClothingColors.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.FootwearStyle, ClampIndex(a.FootwearStyle, gender.Footwear.Count));
        a = WithAppearanceIndex(a, ChargenAppearanceSlot.FootwearColor, ClampIndex(a.FootwearColor, gender.ClothingColors.Count));
        _appearance = a;
    }

    private static uint ClampIndex(uint value, int count)
    {
        if (value == RuntimeCharacterCreationAppearance.Unset)
            return value;
        return value >= (uint)count
            ? unchecked((uint)(count - 1))
            : value;
    }


    private int RollDiceLocked(int min, int max)
    {
        if (min == max)
            return min;
        int lo = Math.Min(min, max);
        int hi = Math.Max(min, max);
        return lo + _random.Next(hi - lo + 1);
    }

    private uint RandomizeIndexExcludingLocked(int count, uint exclude)
    {
        if (count <= 1)
            return 0u;
        uint result;
        do
        {
            result = (uint)_random.Next(count);
        } while (result == exclude);
        return result;
    }

    private double RollShadeLocked() => _random.Next(32768) * (1.0 / 32767.0);

    private void RandomizeAppearanceLocked()
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            return;

        RuntimeCharacterCreationAppearance a = _appearance;
        if (gender.EyeStrips.Count > 0)
            a = a with { EyesStrip = RandomizeIndexExcludingLocked(gender.EyeStrips.Count, a.EyesStrip) };
        if (gender.NoseStrips.Count > 0)
            a = a with { NoseStrip = RandomizeIndexExcludingLocked(gender.NoseStrips.Count, a.NoseStrip) };
        if (gender.MouthStrips.Count > 0)
            a = a with { MouthStrip = RandomizeIndexExcludingLocked(gender.MouthStrips.Count, a.MouthStrip) };
        a = a with { SkinShade = RollShadeLocked(), HairShade = RollShadeLocked() };
        if (gender.HairColors.Count > 0)
            a = a with { HairColor = RandomizeIndexExcludingLocked(gender.HairColors.Count, a.HairColor) };
        if (gender.EyeColors.Count > 0)
            a = a with { EyeColor = RandomizeIndexExcludingLocked(gender.EyeColors.Count, a.EyeColor) };
        if (gender.HairStyles.Count > 0)
            a = a with { HairStyle = RandomizeIndexExcludingLocked(gender.HairStyles.Count, a.HairStyle) };
        _appearance = a;
    }

    private void RandomizeHeadgearLocked(bool excludeCurrent)
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            return;

        int styleCount = gender.Headgears.Count;
        if (styleCount > 0)
        {
            uint current = _appearance.HeadgearStyle;
            int currentPlusOne = current == RuntimeCharacterCreationAppearance.Unset
                ? 0
                : (int)current + 1;
            int rolled = excludeCurrent
                ? (int)RandomizeIndexExcludingLocked(styleCount + 1, (uint)currentPlusOne)
                : _random.Next(styleCount + 1);
            uint newStyle = rolled == 0 ? RuntimeCharacterCreationAppearance.Unset : (uint)(rolled - 1);
            _appearance = _appearance with { HeadgearStyle = newStyle };
        }

        int colorCount = AppearanceSlotCountLocked(ChargenAppearanceSlot.HeadgearColor, gender);
        if (colorCount > 0)
        {
            _appearance = _appearance with
            {
                HeadgearColor = RandomizeIndexExcludingLocked(colorCount, _appearance.HeadgearColor),
            };
        }
        _appearance = _appearance with { HeadgearShade = RollShadeLocked() };
    }

    private void RandomizeShirtLocked()
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            return;
        int styleCount = gender.Shirts.Count;
        if (styleCount > 0)
        {
            _appearance = _appearance with
            {
                ShirtStyle = RandomizeIndexExcludingLocked(styleCount, _appearance.ShirtStyle),
            };
        }
        int colorCount = AppearanceSlotCountLocked(ChargenAppearanceSlot.ShirtColor, gender);
        if (colorCount > 0)
        {
            _appearance = _appearance with
            {
                ShirtColor = RandomizeIndexExcludingLocked(colorCount, _appearance.ShirtColor),
            };
        }
        _appearance = _appearance with { ShirtShade = RollShadeLocked() };
    }

    private void RandomizeTrousersLocked()
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            return;
        int styleCount = gender.Pants.Count;
        if (styleCount > 0)
        {
            _appearance = _appearance with
            {
                TrousersStyle = RandomizeIndexExcludingLocked(styleCount, _appearance.TrousersStyle),
            };
        }
        int colorCount = AppearanceSlotCountLocked(ChargenAppearanceSlot.TrousersColor, gender);
        if (colorCount > 0)
        {
            _appearance = _appearance with
            {
                TrousersColor = RandomizeIndexExcludingLocked(colorCount, _appearance.TrousersColor),
            };
        }
        _appearance = _appearance with { TrousersShade = RollShadeLocked() };
    }

    private void RandomizeFootwearLocked()
    {
        if (!TryGetGenderOptionsLocked(out ChargenGenderOptions? gender))
            return;
        int styleCount = gender.Footwear.Count;
        if (styleCount > 0)
        {
            _appearance = _appearance with
            {
                FootwearStyle = RandomizeIndexExcludingLocked(styleCount, _appearance.FootwearStyle),
            };
        }
        int colorCount = AppearanceSlotCountLocked(ChargenAppearanceSlot.FootwearColor, gender);
        if (colorCount > 0)
        {
            _appearance = _appearance with
            {
                FootwearColor = RandomizeIndexExcludingLocked(colorCount, _appearance.FootwearColor),
            };
        }
        _appearance = _appearance with { FootwearShade = RollShadeLocked() };
    }

    private void RandomizeClothingLocked(bool excludeCurrent)
    {
        RandomizeHeadgearLocked(excludeCurrent);
        RandomizeShirtLocked();
        RandomizeTrousersLocked();
        RandomizeFootwearLocked();
    }

    private void RandomizeTemplateLocked()
    {
        if (_heritageId == 0 || _genderKey == 0)
            return;
        if (!_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? heritage))
            return;

        if (_heritageId == (uint)ChargenHeritageGroup.Olthoi
            || _heritageId == (uint)ChargenHeritageGroup.OlthoiAcid)
        {
            ApplyTemplateLocked(heritage);
            return;
        }

        int count = heritage.Templates.Count;
        if (count <= 1)
            return;

        uint excludeShifted = unchecked(_template - 1u);
        uint picked = RandomizeIndexExcludingLocked(count - 1, excludeShifted) + 1u;
        _template = picked;
        ApplyTemplateLocked(heritage);
    }

    private void RandomizeCharacterLocked()
    {
        ClearSessionState();

        uint heritageId = (uint)RollDiceLocked(1, 4);
        _heritageId = heritageId;
        if (_options.TryGetHeritage(heritageId, out ChargenHeritageOptions? heritage))
            SetHeritageGroupLocked(heritageId, heritage);

        uint genderKey = (uint)RollDiceLocked(1, 2);
        SetGenderLocked(genderKey);

        RandomizeAppearanceLocked();
        RandomizeHeadgearLocked(excludeCurrent: false);
        RandomizeShirtLocked();
        RandomizeTrousersLocked();
        RandomizeFootwearLocked();
        RandomizeTemplateLocked();
        if (_options.TryGetHeritage(_heritageId, out ChargenHeritageOptions? finalHeritage))
            RandomizeStartAreaLocked(finalHeritage);
    }

    internal bool TryRandomizeCharacter()
    {
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            RandomizeCharacterLocked();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TryRandomizeAppearance()
    {
        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0 || _genderKey == 0)
                return false;
            RandomizeAppearanceLocked();
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TryRandomizeClothing()
    {
        lock (_gate)
        {
            if (_disposed || !_active || _heritageId == 0 || _genderKey == 0)
                return false;
            RandomizeClothingLocked(excludeCurrent: true);
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    // ── Town / name / slot ─────────────────────────────────────────────

    internal bool TrySelectStartArea(int index)
    {
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            if (index < 0 || index >= _options.StarterAreas.Count)
                return false;
            _startArea = index;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TrySetName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string bounded = name.Length > 32 ? name[..32] : name;
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            _name = bounded;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    internal bool TrySetSlot(uint slot)
    {
        lock (_gate)
        {
            if (_disposed || !_active)
                return false;
            _slot = slot;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.StateChanged);
        return true;
    }

    // ── Finish / response ───────────────────────────────────────────────

    internal bool TryBeginFinish(
        int rosterCount,
        int slotCount,
        out CharacterCreate.Request request,
        out uint[] skillAdvancementClasses,
        out RuntimeCharacterCreationLocalRefusal refusal,
        bool confirmedUnspentCredits = false)
    {
        request = default;
        skillAdvancementClasses = [];
        bool accepted;
        lock (_gate)
        {
            if (_disposed || !_active)
            {
                refusal = RuntimeCharacterCreationLocalRefusal.None;
                return false;
            }

            string trimmed = _name.Trim();
            _name = trimmed;

            refusal = trimmed.Length == 0
                ? new RuntimeCharacterCreationLocalRefusal(
                    NoName: true, false, false, false)
                : _heritageId == 0 || _genderKey == 0
                    ? new RuntimeCharacterCreationLocalRefusal(
                        false, false, false, false, HeritageOrGenderUnset: true)
                    : !confirmedUnspentCredits && _remainingAttributeCredits > 0
                        ? new RuntimeCharacterCreationLocalRefusal(
                            false, AttributeCreditsUnspent: true, false, false)
                        : _verificationPending
                            ? new RuntimeCharacterCreationLocalRefusal(
                                false, false, AlreadyPending: true, false)
                            : slotCount > 0 && rosterCount >= slotCount
                                ? new RuntimeCharacterCreationLocalRefusal(
                                    false, false, false, RosterFull: true)
                                : RuntimeCharacterCreationLocalRefusal.None;

            _lastLocalRefusal = refusal;
            accepted = !refusal.Any;
            if (accepted)
            {
                _verificationPending = true;
                request = BuildRequestLocked();
                skillAdvancementClasses = ToSkillArrayLocked();
            }
            _revision++;
        }
        Publish(accepted
            ? RuntimeCharacterCreationDeltaKind.FinishSent
            : RuntimeCharacterCreationDeltaKind.FinishRefused);
        return accepted;
    }

    private CharacterCreate.Request BuildRequestLocked() => new(
        _heritageId,
        _genderKey,
        new CharacterCreate.Appearance(
            _appearance.EyesStrip,
            _appearance.NoseStrip,
            _appearance.MouthStrip,
            _appearance.HairColor,
            _appearance.EyeColor,
            _appearance.HairStyle,
            _appearance.HeadgearStyle,
            _appearance.HeadgearColor,
            _appearance.ShirtStyle,
            _appearance.ShirtColor,
            _appearance.TrousersStyle,
            _appearance.TrousersColor,
            _appearance.FootwearStyle,
            _appearance.FootwearColor,
            _appearance.SkinShade,
            _appearance.HairShade,
            _appearance.HeadgearShade,
            _appearance.ShirtShade,
            _appearance.TrousersShade,
            _appearance.FootwearShade),
        _template,
        new CharacterCreate.Attributes(
            checked((uint)_attributes.Strength),
            checked((uint)_attributes.Endurance),
            checked((uint)_attributes.Coordination),
            checked((uint)_attributes.Quickness),
            checked((uint)_attributes.Focus),
            checked((uint)_attributes.Self)),
        _slot,
        0u,
        _name,
        checked((uint)(_startArea < 0 ? 0 : _startArea)),
        IsAdmin: false,
        IsEnvoy: false);

    private uint[] ToSkillArrayLocked()
    {
        IReadOnlyList<uint> wire = _skills.ToWireClasses();
        var array = new uint[wire.Count];
        for (int i = 0; i < array.Length; i++)
            array[i] = wire[i];
        return array;
    }

    internal void ApplyCreationResponse(CharGenVerificationResponse.Parsed response)
    {
        RuntimeCharacterCreationDeltaKind kind;
        lock (_gate)
        {
            if (_disposed || !_active || !_verificationPending)
                return;

            _verificationPending = false;
            if (response.IsOk
                && response.Guid is { } guid
                && response.Name is { } name)
            {
                _lastCreated = new RuntimeCharacterCreationIdentity(guid, name);
                _lastRejection = null;
                kind = RuntimeCharacterCreationDeltaKind.Created;
            }
            else
            {
                string reason = response.AsCode.ToString();
                _lastRejection = new RuntimeCharacterCreationRejection(
                    response.RawCode,
                    response.AsCode,
                    reason,
                    _name);
                kind = RuntimeCharacterCreationDeltaKind.CreationFailed;
            }
            _revision++;
        }
        Publish(kind);
    }

    internal bool TryAcknowledgeRejection()
    {
        lock (_gate)
        {
            if (_disposed || _lastRejection is null)
                return false;
            _lastRejection = null;
            _revision++;
        }
        Publish(RuntimeCharacterCreationDeltaKind.RejectionAcknowledged);
        return true;
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    private void ClearSessionState()
    {
        _heritageId = 0;
        _genderKey = 0;
        _appearance = RuntimeCharacterCreationAppearance.Default;
        _template = RuntimeCharacterCreationSnapshot.TemplateUnset;
        _attributes = default;
        _attributeLockMask = 0u;
        _totalAttributeCredits = 0u;
        _remainingAttributeCredits = 0;
        _totalSkillCredits = 0u;
        _remainingSkillCredits = 0;
        for (uint i = 1; i < ChargenSkillAdvancementSet.SlotCount; i++)
            _skills[i] = ChargenSkillAdvancementClass.Inactive;
        _name = string.Empty;
        _startArea = -1;
        _slot = 0u;
        _verificationPending = false;
        _lastLocalRefusal = RuntimeCharacterCreationLocalRefusal.None;
        _lastRejection = null;
        _lastCreated = null;
    }

    private void Publish(RuntimeCharacterCreationDeltaKind kind)
    {
        RuntimeGenerationToken generation;
        long revision;
        lock (_gate)
        {
            if (_disposed)
                return;
            generation = _generation;
            revision = _revision;
        }
        _events.Publish(generation, revision, kind);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class CharacterCreationEventStream : IDisposable
{
    private readonly object _gate = new();
    private readonly List<RuntimeCharacterCreationDelta> _pending = [];
    private IRuntimeCharacterCreationObserver[] _observers = [];
    private ulong _sequence;
    private bool _dispatching;
    private bool _disposed;

    public IDisposable Subscribe(IRuntimeCharacterCreationObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Array.IndexOf(_observers, observer) >= 0)
            {
                throw new InvalidOperationException(
                    "The character-creation observer is already subscribed.");
            }
            var replacement = new IRuntimeCharacterCreationObserver[_observers.Length + 1];
            Array.Copy(_observers, replacement, _observers.Length);
            replacement[^1] = observer;
            Volatile.Write(ref _observers, replacement);
        }
        return new Subscription(this, observer);
    }

    public void Publish(
        RuntimeGenerationToken generation,
        long revision,
        RuntimeCharacterCreationDeltaKind kind)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _pending.Add(new RuntimeCharacterCreationDelta(
                generation,
                unchecked(++_sequence),
                revision,
                kind));
            if (_dispatching)
                return;
            _dispatching = true;
        }

        int index = 0;
        while (true)
        {
            RuntimeCharacterCreationDelta delta;
            lock (_gate)
            {
                if (index >= _pending.Count)
                {
                    _pending.Clear();
                    _dispatching = false;
                    return;
                }
                delta = _pending[index++];
            }

            foreach (IRuntimeCharacterCreationObserver observer in Volatile.Read(ref _observers))
            {
                try
                {
                    observer.OnCharacterCreationChanged(in delta);
                }
                catch (Exception error)
                {
                    Console.Error.WriteLine(
                        $"runtime: character-creation observer failed: {error.Message}");
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending.Clear();
            _dispatching = false;
            Volatile.Write(ref _observers, []);
        }
    }

    private void Unsubscribe(IRuntimeCharacterCreationObserver observer)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_observers, observer);
            if (index < 0)
                return;
            var replacement = new IRuntimeCharacterCreationObserver[_observers.Length - 1];
            if (index > 0)
                Array.Copy(_observers, 0, replacement, 0, index);
            if (index < _observers.Length - 1)
            {
                Array.Copy(
                    _observers,
                    index + 1,
                    replacement,
                    index,
                    _observers.Length - index - 1);
            }
            Volatile.Write(ref _observers, replacement);
        }
    }

    private sealed class Subscription(
        CharacterCreationEventStream owner,
        IRuntimeCharacterCreationObserver observer)
        : IDisposable
    {
        private CharacterCreationEventStream? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Unsubscribe(observer);
    }
}
