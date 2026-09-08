using System;
using System.Collections.Generic;

namespace AcDream.Core.Spells;


public enum MagicSchool : uint
{
    None = 0,
    WarMagic      = 1,
    LifeMagic     = 2,
    ItemEnchantment     = 3,
    CreatureEnchantment = 4,
    VoidMagic           = 5,
}

public enum SpellTargetType : uint
{
    None               = 0,
    Self               = 1,
    Item               = 2,
    Creature           = 3,
    Object             = 4,
    SelfOrItem         = 5,
    Undef              = 6,
    OtherItem          = 7,    // targeted item but not your own
}

public enum SpellCategory : uint
{
    Undef = 0,
}

[Flags]
public enum SpellFlags : uint
{
    None               = 0,
    Resistable         = 0x00000001,
    PKSensitive        = 0x00000002,
    Beneficial         = 0x00000004,
    SelfTargeted       = 0x00000008,
    Reversed           = 0x00000010,
    NotIndoors         = 0x00000020,
    NotOutdoors        = 0x00000040,
    NotResearchable    = 0x00000080,
    Projectile         = 0x00000100,
    CreatureSpell      = 0x00000200,
    ExcludedFromItemDescriptions = 0x00000400,
    IgnoresManaConversion = 0x00000800,
    NonTrackingProjectile = 0x00001000,
    FellowshipSpell    = 0x00002000,
    FastCast           = 0x00004000,
    IndoorLongRange    = 0x00008000,
    DamageOverTime     = 0x00010000,
    // more flags in r01 §1
}

public sealed class SpellDatEntry
{
    public uint   SpellId          { get; init; }
    public string Name             { get; init; } = "";
    public string Description      { get; init; } = "";
    public MagicSchool School      { get; init; }
    public int    Power            { get; init; }          // difficulty / mastery
    public float  CastingTime      { get; init; }          // seconds
    public float  Duration         { get; init; }          // for enchants
    public int    BaseMana         { get; init; }
    public int    ManaMod          { get; init; }          // per extra target
    public int    ManaConversionBase { get; init; }
    public float  ManaConversionMod { get; init; }
    public SpellTargetType TargetType { get; init; }
    public SpellCategory Category  { get; init; }
    public SpellFlags Flags        { get; init; }
    public IReadOnlyList<int> FormulaComponentIds { get; init; } = Array.Empty<int>();
    public int    RangeConstant    { get; init; }
    public int    EconomyMod       { get; init; }
    public uint   Icon             { get; init; }          // 0x06xxxxxx
    public uint   SpellStatModKey  { get; init; }          // what property it buffs
    public int    SpellStatModVal  { get; init; }
}

public sealed class SpellComponentEntry
{
    public int    ComponentId  { get; init; }
    public string Name         { get; init; } = "";
    public uint   Icon         { get; init; }
    public double CdmBonus     { get; init; }
    public double ManaMod      { get; init; }
    public int    Type         { get; init; }           // 1=scarab, 2=herb, 3=talisman, 4=taper, 5=pwax
}

public enum SpellCastPhase
{
    Idle,
    Preparing,    // server validating
    Casting,      // syllables being played
    Releasing,
    Fizzled,
    Complete,
}

public sealed class SpellCastStateMachine
{
    public SpellCastPhase Phase      { get; private set; } = SpellCastPhase.Idle;
    public uint           SpellId    { get; private set; }
    public uint?          TargetGuid { get; private set; }
    public double         StartedAt  { get; private set; }
    public double         CastDuration { get; private set; }

    public event Action<SpellCastStateMachine>? OnPhaseChanged;

    public void BeginCast(uint spellId, uint? targetGuid, double castDurationSec, double nowSec)
    {
        SpellId = spellId;
        TargetGuid = targetGuid;
        StartedAt = nowSec;
        CastDuration = castDurationSec;
        TransitionTo(SpellCastPhase.Preparing);
    }

    public void ServerAckCastingStart() => TransitionTo(SpellCastPhase.Casting);

    public void ServerReleaseCast() => TransitionTo(SpellCastPhase.Releasing);

    public void ServerCompleteCast() => TransitionTo(SpellCastPhase.Complete);

    public void ServerFizzle() => TransitionTo(SpellCastPhase.Fizzled);

    private void TransitionTo(SpellCastPhase next)
    {
        Phase = next;
        OnPhaseChanged?.Invoke(this);
    }
}

public sealed class ActiveBuff
{
    public uint         SpellId    { get; init; }
    public uint         CasterGuid { get; init; }
    public SpellCategory Category  { get; init; }
    public int          Power      { get; init; }
    public double       StartedAt  { get; init; }
    public double       Duration   { get; init; }
    public double       EndsAt     => StartedAt + Duration;
    public int          StatModKey { get; init; }
    public int          StatModValue { get; init; }
}

public static class SpellMath
{
    public static double ChanceOfSuccess(int skill, int difficulty)
    {
        if (skill < difficulty - 50) return 0.0;
        double x = -0.07 * (skill - difficulty);
        return 1.0 / (1.0 + Math.Exp(x));
    }

    public static int ComputeManaCost(SpellDatEntry spell, int numTargets,
        int manaConvSkill, Random rng)
    {
        double cost = spell.BaseMana + spell.ManaMod * Math.Max(0, numTargets - 1);

        // First reduction roll at half difficulty
        if (rng.NextDouble() < ChanceOfSuccess(manaConvSkill, spell.Power / 2))
            cost *= 0.5;
        // Second reduction roll at full difficulty
        if (rng.NextDouble() < ChanceOfSuccess(manaConvSkill, spell.Power))
            cost *= 0.5;

        return (int)Math.Max(1, Math.Round(cost));
    }
}
