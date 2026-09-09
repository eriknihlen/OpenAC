using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Items;
using AcDream.Core.Physics;
using AcDream.Core.Properties;
using AcDream.Core.Spells;

namespace AcDream.Core.Player;

public sealed class LocalPlayerState
{
    public enum VitalKind
    {
        Health,
        Stamina,
        Mana,
    }

    public enum AttributeKind
    {
        Strength,
        Endurance,
        Quickness,
        Coordination,
        Focus,
        Self,
    }

    public readonly record struct AttributeSnapshot(uint Ranks, uint Start, uint Xp)
    {
        public uint Current => Ranks + Start;
    }

    public readonly record struct VitalSnapshot(uint Ranks, uint Start, uint Xp, uint Current);

    public readonly record struct SkillSnapshot(
        uint SkillId,
        uint Ranks,
        uint Status,
        uint Xp,
        uint Init,
        uint Resistance,
        double LastUsed,
        uint FormulaBonus)
    {
        public uint BaseLevel => FormulaBonus + Init + Ranks;
        public uint CurrentLevel => BaseLevel;
    }

    private VitalSnapshot? _health;
    private VitalSnapshot? _stamina;
    private VitalSnapshot? _mana;
    private readonly Dictionary<AttributeKind, AttributeSnapshot> _attrs = new();
    private readonly Dictionary<uint, SkillSnapshot> _skills = new();
    private readonly Dictionary<uint, Position> _positions = new();
    private PropertyBundle _properties = new();
    private readonly Spellbook? _spellbook;

    public LocalPlayerState(Spellbook? spellbook = null)
    {
        _spellbook = spellbook;
    }

    public event System.Action<VitalKind>? Changed;

    public event System.Action<AttributeKind>? AttributeChanged;

    public event System.Action? CharacterChanged;

    public static VitalKind? VitalIdToKind(uint vitalId) => vitalId switch
    {
        1u or 2u or 7u => VitalKind.Health,
        3u or 4u or 8u => VitalKind.Stamina,
        5u or 6u or 9u => VitalKind.Mana,
        _              => null,
    };

    public static AttributeKind? AttributeIdToKind(uint atType) => atType switch
    {
        1u => AttributeKind.Strength,
        2u => AttributeKind.Endurance,
        3u => AttributeKind.Quickness,
        4u => AttributeKind.Coordination,
        5u => AttributeKind.Focus,
        6u => AttributeKind.Self,
        _  => null,
    };

    /// <summary>Snapshot for a vital, or <c>null</c> if never received.</summary>
    public VitalSnapshot? Get(VitalKind kind) => kind switch
    {
        VitalKind.Health  => _health,
        VitalKind.Stamina => _stamina,
        VitalKind.Mana    => _mana,
        _                 => null,
    };

    /// <summary>Snapshot for a primary attribute, or <c>null</c> if never received.</summary>
    public AttributeSnapshot? GetAttribute(AttributeKind kind) =>
        _attrs.TryGetValue(kind, out var a) ? a : null;

    public Spellbook? Spellbook => _spellbook;

    public int? GetEffectiveAttribute(AttributeKind kind)
    {
        AttributeSnapshot? attr = GetAttribute(kind);
        if (attr is null) return null;
        uint baseValue = attr.Value.Current;
        if (_spellbook is null) return (int)baseValue;
        EnchantmentMath.VitalMod mod = _spellbook.GetAttributeMod(AttributeKindToId(kind));
        return EnchantmentMath.EnchantAttribute(mod, baseValue);
    }

    public static uint AttributeKindToId(AttributeKind kind) => kind switch
    {
        AttributeKind.Strength     => 1u,
        AttributeKind.Endurance    => 2u,
        AttributeKind.Quickness    => 3u,
        AttributeKind.Coordination => 4u,
        AttributeKind.Focus        => 5u,
        AttributeKind.Self         => 6u,
        _                          => 0u,
    };

    public int? GetEffectiveSkill(uint skillId)
        => GetSkillValue(skillId)?.EffectiveLevel;

    public PlayerSkillMath.Value? GetSkillValue(
        uint skillId,
        PropertyBundle? properties = null)
    {
        SkillSnapshot? skill = GetSkill(skillId);
        if (skill is null) return null;

        PlayerSkillMath.AugmentationBonuses augmentations =
            PlayerSkillMath.AugmentationBonuses.FromProperties(
                properties ?? _properties);
        EnchantmentMath.VitalMod mod = _spellbook?.GetSkillMod(skillId)
            ?? EnchantmentMath.VitalMod.Identity;
        float vitae = _spellbook is null
            ? 1f
            : EnchantmentMath.GetVitaeMultiplier(
                _spellbook.ActiveEnchantments);
        int intrinsic = checked((int)Math.Min(int.MaxValue, skill.Value.CurrentLevel));
        return PlayerSkillMath.Calculate(
            intrinsic,
            intrinsic + AttributeEnchantmentSkillDelta(skillId),
            skillId,
            skill.Value.Status,
            augmentations,
            mod,
            vitae);
    }

    public int AttributeEnchantmentSkillDelta(uint skillId)
    {
        if (SkillFormulaBonusResolver is not { } resolver
            || _spellbook is null
            || !_skills.TryGetValue(skillId, out SkillSnapshot snap))
        {
            return 0;
        }
        uint enchanted = resolver(skillId, EnchantedAttributeCurrentsById());
        return checked((int)Math.Min(int.MaxValue, enchanted))
            - checked((int)Math.Min(int.MaxValue, snap.FormulaBonus));
    }

    public IReadOnlyDictionary<uint, uint> EnchantedAttributeCurrentsById()
    {
        var currents = new Dictionary<uint, uint>(_attrs.Count);
        foreach (AttributeKind kind in _attrs.Keys)
        {
            int value = GetEffectiveAttribute(kind) ?? 0;
            currents[(uint)kind + 1u] = (uint)Math.Max(0, value);
        }
        return currents;
    }

    public int GetSkillVitaeModifier(uint skillId)
        => GetSkillValue(skillId)?.VitaeModifier ?? 0;

    public PropertyBundle Properties => _properties;

    /// <summary>All known skill snapshots, keyed by SkillId.</summary>
    public IReadOnlyDictionary<uint, SkillSnapshot> Skills => _skills;

    public IReadOnlyDictionary<uint, Position> Positions => _positions;

    public Position? GetPosition(uint positionType) =>
        _positions.TryGetValue(positionType, out var value) ? value : null;

    public void OnPositions(IReadOnlyDictionary<uint, Position> positions)
    {
        _positions.Clear();
        foreach (var pair in positions)
            _positions[pair.Key] = pair.Value;
        CharacterChanged?.Invoke();
    }

    /// <summary>Snapshot for one skill, or <c>null</c> if it has not arrived yet.</summary>
    public SkillSnapshot? GetSkill(uint skillId) =>
        _skills.TryGetValue(skillId, out var s) ? s : null;

    public uint? GetMaxApprox(VitalKind kind)
    {
        uint? baseValue = GetMaxBeforeSecondaryEnchantments(kind);
        if (baseValue is not uint unbuffed) return null;
        if (unbuffed == 0) return 0;

        var mod = _spellbook?.GetVitalMod(StatKeyForKind(kind))
                  ?? EnchantmentMath.VitalMod.Identity;
        float buffed = (unbuffed * mod.Multiplier) + mod.Additive;
        uint minFloor = unbuffed >= 5 ? 5u : 1u;
        if (buffed < minFloor) buffed = minFloor;
        return (uint)buffed;
    }

    public uint? GetBaseMaxApprox(VitalKind kind)
    {
        VitalSnapshot? vital = Get(kind);
        if (vital is null) return null;
        return vital.Value.Ranks
            + vital.Value.Start
            + AttributeContribution(kind, effective: false)
            + GearHealthBonus(kind);
    }

    private uint? GetMaxBeforeSecondaryEnchantments(VitalKind kind)
    {
        VitalSnapshot? vital = Get(kind);
        if (vital is null) return null;
        return vital.Value.Ranks
            + vital.Value.Start
            + AttributeContribution(kind, effective: true)
            + GearHealthBonus(kind);
    }

    public int GetVitalVitaeModifier(VitalKind kind)
    {
        if (_spellbook is null
            || GetBaseMaxApprox(kind) is not uint baseValue)
        {
            return 0;
        }

        return EnchantmentMath.SkillVitaeModifier(
            EnchantmentMath.GetVitaeMultiplier(
                _spellbook.ActiveEnchantments),
            baseValue);
    }

    private static uint StatKeyForKind(VitalKind kind) => kind switch
    {
        VitalKind.Health  => EnchantmentMath.StatKey.MaxHealth,
        VitalKind.Stamina => EnchantmentMath.StatKey.MaxStamina,
        VitalKind.Mana    => EnchantmentMath.StatKey.MaxMana,
        _                 => 0u,
    };

    /// <summary>Stamina percent (0..1) or null when not yet received.</summary>
    public float? StaminaPercent => Percent(VitalKind.Stamina);

    /// <summary>Mana percent (0..1) or null when not yet received.</summary>
    public float? ManaPercent => Percent(VitalKind.Mana);

    /// <summary>Health percent (0..1) or null when not yet received.</summary>
    public float? HealthPercent => Percent(VitalKind.Health);

    private float? Percent(VitalKind kind)
    {
        var v = Get(kind);
        if (v is null) return null;
        uint? max = GetMaxApprox(kind);
        if (max is not uint m || m == 0) return null;
        float r = (float)v.Value.Current / m;
        if (r < 0f) r = 0f;
        else if (r > 1f) r = 1f;
        return r;
    }

    public void OnVitalUpdate(uint vitalId, uint ranks, uint start, uint xp, uint current)
    {
        if (VitalIdToKind(vitalId) is not VitalKind kind) return;
        var snap = new VitalSnapshot(ranks, start, xp, current);
        switch (kind)
        {
            case VitalKind.Health:  _health  = snap; break;
            case VitalKind.Stamina: _stamina = snap; break;
            case VitalKind.Mana:    _mana    = snap; break;
        }
        Changed?.Invoke(kind);
    }

    public void OnVitalCurrent(uint vitalId, uint current)
    {
        if (VitalIdToKind(vitalId) is not VitalKind kind) return;
        VitalSnapshot? existing = Get(kind);
        if (existing is not VitalSnapshot prev) return;
        var snap = prev with { Current = current };
        switch (kind)
        {
            case VitalKind.Health:  _health  = snap; break;
            case VitalKind.Stamina: _stamina = snap; break;
            case VitalKind.Mana:    _mana    = snap; break;
        }
        Changed?.Invoke(kind);
    }

    public void OnAttributeUpdate(uint atType, uint ranks, uint start, uint xp)
    {
        if (AttributeIdToKind(atType) is not AttributeKind kind) return;
        _attrs[kind] = new AttributeSnapshot(ranks, start, xp);
        RecomputeSkillFormulaBonuses();
        AttributeChanged?.Invoke(kind);
        switch (kind)
        {
            case AttributeKind.Endurance:
                Changed?.Invoke(VitalKind.Health);
                Changed?.Invoke(VitalKind.Stamina);
                break;
            case AttributeKind.Self:
                Changed?.Invoke(VitalKind.Mana);
                break;
        }
        CharacterChanged?.Invoke();
    }

    /// <summary>Replace the local player's top-level property snapshot from PlayerDescription.</summary>
    public void OnProperties(PropertyBundle properties)
    {
        _properties = properties.Clone();
        CharacterChanged?.Invoke();
    }

    public void OnInt64PropertyUpdate(uint propertyId, long value)
    {
        _properties.Int64s[propertyId] = value;
        CharacterChanged?.Invoke();
    }

    /// <summary>Apply or replace one PlayerDescription skill entry.</summary>
    public void OnSkillUpdate(
        uint skillId,
        uint ranks,
        uint status,
        uint xp,
        uint init,
        uint resistance,
        double lastUsed,
        uint formulaBonus)
    {
        _skills[skillId] = new SkillSnapshot(
            skillId, ranks, status, xp, init, resistance, lastUsed, formulaBonus);
        CharacterChanged?.Invoke();
    }

    public void OnSkillWireUpdate(
        uint skillId,
        uint ranks,
        uint status,
        uint xp,
        uint init,
        uint resistance,
        double lastUsed)
    {
        uint formulaBonus = SkillFormulaBonusResolver is { } resolver
            ? resolver(skillId, AttributeCurrentsById())
            : _skills.TryGetValue(skillId, out var prev)
                ? prev.FormulaBonus
                : 0u;
        _skills[skillId] = new SkillSnapshot(
            skillId, ranks, status, xp, init, resistance, lastUsed, formulaBonus);
        CharacterChanged?.Invoke();
    }

    public Func<uint /*skillId*/, IReadOnlyDictionary<uint, uint> /*attrCurrents*/, uint>?
        SkillFormulaBonusResolver { get; set; }

    public void RecomputeSkillFormulaBonuses()
    {
        if (SkillFormulaBonusResolver is not { } resolver || _skills.Count == 0)
            return;
        IReadOnlyDictionary<uint, uint> currents = AttributeCurrentsById();
        foreach (uint skillId in _skills.Keys.ToArray())
        {
            SkillSnapshot snap = _skills[skillId];
            uint bonus = resolver(skillId, currents);
            if (bonus != snap.FormulaBonus)
                _skills[skillId] = snap with { FormulaBonus = bonus };
        }
    }

    public IReadOnlyDictionary<uint, uint> AttributeCurrentsById()
    {
        var currents = new Dictionary<uint, uint>(_attrs.Count);
        foreach ((AttributeKind kind, AttributeSnapshot snap) in _attrs)
            currents[(uint)kind + 1u] = snap.Current;
        return currents;
    }

    public (int RunSkill, int JumpSkill) MovementSkillTotals()
    {
        int run = -1, jump = -1;
        if (_skills.TryGetValue(24u, out var runSnap))
            run = (int)(runSnap.FormulaBonus + runSnap.Init + runSnap.Ranks);
        if (_skills.TryGetValue(22u, out var jumpSnap))
            jump = (int)(jumpSnap.FormulaBonus + jumpSnap.Init + jumpSnap.Ranks);
        return (run, jump);
    }

    public void Clear()
    {
        _health = null;
        _stamina = null;
        _mana = null;
        _attrs.Clear();
        _skills.Clear();
        _positions.Clear();
        _properties = new PropertyBundle();

        Changed?.Invoke(VitalKind.Health);
        Changed?.Invoke(VitalKind.Stamina);
        Changed?.Invoke(VitalKind.Mana);
        foreach (AttributeKind kind in Enum.GetValues<AttributeKind>())
            AttributeChanged?.Invoke(kind);
        CharacterChanged?.Invoke();
    }

    private static uint SaturatingAdd(uint value, uint delta)
        => uint.MaxValue - value < delta ? uint.MaxValue : value + delta;

    private static uint SaturatingAdd(uint value, ulong delta)
        => delta > uint.MaxValue - value ? uint.MaxValue : value + (uint)delta;


    private uint AttributeContribution(VitalKind kind, bool effective)
    {
        uint endurance = GetAttrCurrent(AttributeKind.Endurance, effective);
        uint self = GetAttrCurrent(AttributeKind.Self, effective);
        switch (kind)
        {
            case VitalKind.Health:
                // SecondaryAttributeTable's SkillFormula is
                // floor((Endurance / 2) + 0.5), not integer truncation.
                return (endurance / 2u) + (endurance & 1u);
            case VitalKind.Stamina:
                return endurance;
            case VitalKind.Mana:
                return self;
            default:
                return 0u;
        }
    }

    private uint GetAttrCurrent(AttributeKind kind, bool effective)
    {
        if (!_attrs.TryGetValue(kind, out var attribute)) return 0u;
        if (!effective || _spellbook is null) return attribute.Current;
        int value = GetEffectiveAttribute(kind) ?? 0;
        return value > 0 ? (uint)value : 0u;
    }

    private uint GearHealthBonus(VitalKind kind)
    {
        if (kind != VitalKind.Health) return 0u;
        int value = _properties.GetInt((uint)PropertyInt.GearMaxHealth);
        return value > 0 ? (uint)value : 0u;
    }
}
