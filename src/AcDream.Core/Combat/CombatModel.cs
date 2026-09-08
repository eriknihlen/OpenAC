using System;
using System.Collections.Generic;
using AcDream.Core.Items;

namespace AcDream.Core.Combat;


[Flags]
public enum CombatMode
{
    Undef     = 0,
    NonCombat = 0x01,
    Melee     = 0x02,
    Missile   = 0x04,
    Magic     = 0x08,

    ValidCombat  = NonCombat | Melee | Missile | Magic,
    CombatCombat = Melee | Missile | Magic,
}

public enum AttackHeight
{
    Undef  = 0,
    High   = 1,
    Medium = 2,
    Low    = 3,
}

public enum CombatAttackAction
{
    Low,
    Medium,
    High,
}

public readonly record struct DefaultCombatModeDecision(
    CombatMode Mode,
    ClientObject? IncompatibleHeldItem);

public static class CombatInputPlanner
{
    public const uint DualWieldCombatStyle = 0x80000046u;
    public const uint ReadyForwardCommand = 0x41000003u;
    public const double AttackPowerUpSeconds = 1.0;
    public const double DualWieldPowerUpSeconds = 0.8;

    public static bool PlayerInReadyPositionForAttack(
        CombatMode mode,
        uint currentStyle,
        uint forwardCommand)
    {
        if (mode == CombatMode.Melee)
            return true;
        if (mode != CombatMode.Missile || forwardCommand != ReadyForwardCommand)
            return false;
        return currentStyle is 0x8000003Fu
            or 0x80000041u
            or 0x80000043u
            or 0x80000047u
            or 0x80000138u
            or 0x80000139u;
    }

    private const EquipMask PrimaryWeaponLocations =
        EquipMask.MeleeWeapon | EquipMask.MissileWeapon | EquipMask.TwoHanded;

    public static CombatMode GetDefaultCombatMode(
        IReadOnlyList<ClientObject> orderedPlayerContents) =>
        GetDefaultCombatModeDecision(orderedPlayerContents).Mode;

    public static DefaultCombatModeDecision GetDefaultCombatModeDecision(
        IReadOnlyList<ClientObject> orderedPlayerContents)
    {
        ArgumentNullException.ThrowIfNull(orderedPlayerContents);

        ClientObject? weapon = GetObjectAtLocation(
            orderedPlayerContents, PrimaryWeaponLocations);
        if (weapon is not null)
        {
            return new(
                weapon.CombatUse == 2
                    ? CombatMode.Missile
                    : CombatMode.Melee,
                IncompatibleHeldItem: null);
        }

        ClientObject? held = GetObjectAtLocation(
            orderedPlayerContents, EquipMask.Held);
        if (held is null)
            return new(CombatMode.Melee, IncompatibleHeldItem: null);

        return (held.Type & ItemType.Caster) != 0
            ? new(CombatMode.Magic, IncompatibleHeldItem: null)
            : new(CombatMode.NonCombat, held);
    }

    private static ClientObject? GetObjectAtLocation(
        IReadOnlyList<ClientObject> orderedPlayerContents,
        EquipMask locationMask)
    {
        for (int i = 0; i < orderedPlayerContents.Count; i++)
        {
            ClientObject candidate = orderedPlayerContents[i];
            if ((candidate.CurrentlyEquippedLocation & locationMask) != 0)
                return candidate;
        }

        return null;
    }

    public static CombatMode ToggleMode(
        CombatMode currentMode,
        CombatMode defaultCombatMode = CombatMode.Melee)
    {
        if (currentMode != CombatMode.NonCombat)
            return CombatMode.NonCombat;

        return defaultCombatMode;
    }

    public static bool SupportsTargetedAttack(CombatMode mode) =>
        mode == CombatMode.Melee || mode == CombatMode.Missile;

    public static AttackHeight HeightFor(CombatAttackAction action) => action switch
    {
        CombatAttackAction.Low => AttackHeight.Low,
        CombatAttackAction.Medium => AttackHeight.Medium,
        CombatAttackAction.High => AttackHeight.High,
        _ => AttackHeight.Medium,
    };
}

[Flags]
public enum AttackType : uint
{
    None                  = 0,
    Punch                 = 0x0001,
    Thrust                = 0x0002,
    Slash                 = 0x0004,
    Kick                  = 0x0008,
    OffhandPunch          = 0x0010,
    DoubleSlash           = 0x0020,
    TripleSlash           = 0x0040,
    DoubleThrust          = 0x0080,
    TripleThrust          = 0x0100,
    OffhandThrust         = 0x0200,
    OffhandSlash          = 0x0400,
    OffhandDoubleSlash    = 0x0800,
    OffhandTripleSlash    = 0x1000,
    OffhandDoubleThrust   = 0x2000,
    OffhandTripleThrust   = 0x4000,
    Unarmed               = Punch | Kick | OffhandPunch,
    MultiStrike           = DoubleSlash | TripleSlash | DoubleThrust | TripleThrust
                          | OffhandDoubleSlash | OffhandTripleSlash
                          | OffhandDoubleThrust | OffhandTripleThrust,
}

[Flags]
public enum DamageType : uint
{
    Undef    = 0,
    Slash    = 0x0001,
    Pierce   = 0x0002,
    Bludgeon = 0x0004,
    Cold     = 0x0008,
    Fire     = 0x0010,
    Acid     = 0x0020,
    Electric = 0x0040,
    Health   = 0x0080,
    Stamina  = 0x0100,
    Mana     = 0x0200,
    Nether   = 0x0400,
    Base     = 0x10000000,
}

public enum BodyPart
{
    Head     = 0,
    Chest    = 1,
    Abdomen  = 2,
    UpperArm = 3,
    LowerArm = 4,
    Hand     = 5,
    UpperLeg = 6,
    LowerLeg = 7,
    Foot     = 8,
}

public readonly record struct DamageEvent(
    uint AttackerGuid,
    uint TargetGuid,
    AttackType AttackType,
    DamageType DamageType,
    BodyPart BodyPart,
    int DamageDealt,
    int PostResistDamage,
    bool WasCritical,
    bool WasEvaded,
    bool WasResisted,
    float AccuracyModUsed,
    float PowerModUsed);

public static class CombatMath
{
    public static float PowerModMelee(float powerLevel) => powerLevel + 0.5f;

    // ── Accuracy bar (missile) ─────────────────────────────────────
    // AccuracyMod = AccuracyLevel + 0.6, so [0.6, 1.6].
    public static float AccuracyModMissile(float accuracyLevel) => accuracyLevel + 0.6f;

    public static double HitChancePhysical(int attackSkill, int defenseSkill)
        => 1.0 - 1.0 / (1.0 + Math.Exp(0.03 * (attackSkill - defenseSkill)));

    public static double HitChanceMagic(int attackSkill, int defenseSkill)
        => 1.0 - 1.0 / (1.0 + Math.Exp(0.07 * (attackSkill - defenseSkill)));

    public const double PhysicalCritBase = 0.10;   // 10%
    public const double MagicCritBase    = 0.05;   // 5%

    public static int ComputeDamage(
        float weaponDamageMin, float weaponDamageMax,
        int attributeBonus,
        float powerMod,                   // PowerModMelee or AccuracyModMissile
        float skillMod,                   // skill-based bonus (weapon skill)
        bool isCritical,
        float critMultiplier,
        float armorReduction,             // damage reduction from armor
        float resistMultiplier,           // 0..1 multiplier from buffs / natural resist
        Random rng)
    {
        // Base weapon roll
        double baseDmg = rng.NextDouble() * (weaponDamageMax - weaponDamageMin) + weaponDamageMin;

        // Apply attribute bonus + power mod + skill mod
        double raw = (baseDmg + attributeBonus) * powerMod * skillMod;

        if (isCritical) raw *= critMultiplier;

        raw = Math.Max(0, raw - armorReduction);

        // Resist multiplier (<1 reduces, >1 amplifies)
        raw *= resistMultiplier;

        return (int)Math.Max(0, Math.Round(raw));
    }
}

public sealed class ArmorBuild
{
    public int ALHead     { get; set; }
    public int ALChest    { get; set; }
    public int ALAbdomen  { get; set; }
    public int ALUpperArm { get; set; }
    public int ALLowerArm { get; set; }
    public int ALHand     { get; set; }
    public int ALUpperLeg { get; set; }
    public int ALLowerLeg { get; set; }
    public int ALFoot     { get; set; }

    public int Get(BodyPart bp) => bp switch
    {
        BodyPart.Head     => ALHead,
        BodyPart.Chest    => ALChest,
        BodyPart.Abdomen  => ALAbdomen,
        BodyPart.UpperArm => ALUpperArm,
        BodyPart.LowerArm => ALLowerArm,
        BodyPart.Hand     => ALHand,
        BodyPart.UpperLeg => ALUpperLeg,
        BodyPart.LowerLeg => ALLowerLeg,
        BodyPart.Foot     => ALFoot,
        _ => 0,
    };
}
