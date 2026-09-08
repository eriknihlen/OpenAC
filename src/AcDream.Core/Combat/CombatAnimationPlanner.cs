using AcDream.Core.Physics;
using Drw = DatReaderWriter.Enums.MotionCommand;

namespace AcDream.Core.Combat;

public static class CombatAnimationPlanner
{
    public static CombatAnimationPlan PlanForEvent(CombatAnimationEvent combatEvent)
    {
        _ = combatEvent;
        return CombatAnimationPlan.None;
    }

    public static CombatAnimationPlan PlanFromWireCommand(ushort wireCommand, float speedMod = 1f)
    {
        uint fullCommand = MotionCommandResolver.ReconstructFullCommand(wireCommand);
        return PlanFromFullCommand(fullCommand, speedMod);
    }

    public static CombatAnimationPlan PlanFromFullCommand(uint fullCommand, float speedMod = 1f)
    {
        var kind = ClassifyMotionCommand(fullCommand);
        if (kind == CombatAnimationKind.None)
            return CombatAnimationPlan.None;

        return new CombatAnimationPlan(
            kind,
            AnimationCommandRouter.Classify(fullCommand),
            fullCommand,
            speedMod);
    }

    public static CombatAnimationKind ClassifyMotionCommand(uint fullCommand)
    {
        return fullCommand switch
        {
            CombatAnimationMotionCommands.HandCombat
                or CombatAnimationMotionCommands.SwordCombat
                or CombatAnimationMotionCommands.SwordShieldCombat
                or CombatAnimationMotionCommands.TwoHandedSwordCombat
                or CombatAnimationMotionCommands.TwoHandedStaffCombat
                or CombatAnimationMotionCommands.BowCombat
                or CombatAnimationMotionCommands.CrossbowCombat
                or CombatAnimationMotionCommands.SlingCombat
                or CombatAnimationMotionCommands.DualWieldCombat
                or CombatAnimationMotionCommands.ThrownWeaponCombat
                or CombatAnimationMotionCommands.AtlatlCombat
                or CombatAnimationMotionCommands.ThrownShieldCombat
                or CombatAnimationMotionCommands.Magic => CombatAnimationKind.CombatStance,

            CombatAnimationMotionCommands.ThrustMed
                or CombatAnimationMotionCommands.ThrustLow
                or CombatAnimationMotionCommands.ThrustHigh
                or CombatAnimationMotionCommands.SlashHigh
                or CombatAnimationMotionCommands.SlashMed
                or CombatAnimationMotionCommands.SlashLow
                or CombatAnimationMotionCommands.BackhandHigh
                or CombatAnimationMotionCommands.BackhandMed
                or CombatAnimationMotionCommands.BackhandLow
                or CombatAnimationMotionCommands.DoubleSlashLow
                or CombatAnimationMotionCommands.DoubleSlashMed
                or CombatAnimationMotionCommands.DoubleSlashHigh
                or CombatAnimationMotionCommands.TripleSlashLow
                or CombatAnimationMotionCommands.TripleSlashMed
                or CombatAnimationMotionCommands.TripleSlashHigh
                or CombatAnimationMotionCommands.DoubleThrustLow
                or CombatAnimationMotionCommands.DoubleThrustMed
                or CombatAnimationMotionCommands.DoubleThrustHigh
                or CombatAnimationMotionCommands.TripleThrustLow
                or CombatAnimationMotionCommands.TripleThrustMed
                or CombatAnimationMotionCommands.TripleThrustHigh
                or CombatAnimationMotionCommands.OffhandSlashHigh
                or CombatAnimationMotionCommands.OffhandSlashMed
                or CombatAnimationMotionCommands.OffhandSlashLow
                or CombatAnimationMotionCommands.OffhandThrustHigh
                or CombatAnimationMotionCommands.OffhandThrustMed
                or CombatAnimationMotionCommands.OffhandThrustLow
                or CombatAnimationMotionCommands.OffhandDoubleSlashLow
                or CombatAnimationMotionCommands.OffhandDoubleSlashMed
                or CombatAnimationMotionCommands.OffhandDoubleSlashHigh
                or CombatAnimationMotionCommands.OffhandTripleSlashLow
                or CombatAnimationMotionCommands.OffhandTripleSlashMed
                or CombatAnimationMotionCommands.OffhandTripleSlashHigh
                or CombatAnimationMotionCommands.OffhandDoubleThrustLow
                or CombatAnimationMotionCommands.OffhandDoubleThrustMed
                or CombatAnimationMotionCommands.OffhandDoubleThrustHigh
                or CombatAnimationMotionCommands.OffhandTripleThrustLow
                or CombatAnimationMotionCommands.OffhandTripleThrustMed
                or CombatAnimationMotionCommands.OffhandTripleThrustHigh
                or CombatAnimationMotionCommands.OffhandKick
                or CombatAnimationMotionCommands.PunchFastHigh
                or CombatAnimationMotionCommands.PunchFastMed
                or CombatAnimationMotionCommands.PunchFastLow
                or CombatAnimationMotionCommands.PunchSlowHigh
                or CombatAnimationMotionCommands.PunchSlowMed
                or CombatAnimationMotionCommands.PunchSlowLow
                or CombatAnimationMotionCommands.OffhandPunchFastHigh
                or CombatAnimationMotionCommands.OffhandPunchFastMed
                or CombatAnimationMotionCommands.OffhandPunchFastLow
                or CombatAnimationMotionCommands.OffhandPunchSlowHigh
                or CombatAnimationMotionCommands.OffhandPunchSlowMed
                or CombatAnimationMotionCommands.OffhandPunchSlowLow => CombatAnimationKind.MeleeSwing,

            CombatAnimationMotionCommands.Shoot
                or CombatAnimationMotionCommands.MissileAttack1
                or CombatAnimationMotionCommands.MissileAttack2
                or CombatAnimationMotionCommands.MissileAttack3
                or CombatAnimationMotionCommands.Reload => CombatAnimationKind.MissileAttack,

            CombatAnimationMotionCommands.AttackHigh1
                or CombatAnimationMotionCommands.AttackMed1
                or CombatAnimationMotionCommands.AttackLow1
                or CombatAnimationMotionCommands.AttackHigh2
                or CombatAnimationMotionCommands.AttackMed2
                or CombatAnimationMotionCommands.AttackLow2
                or CombatAnimationMotionCommands.AttackHigh3
                or CombatAnimationMotionCommands.AttackMed3
                or CombatAnimationMotionCommands.AttackLow3
                or CombatAnimationMotionCommands.AttackHigh4
                or CombatAnimationMotionCommands.AttackMed4
                or CombatAnimationMotionCommands.AttackLow4
                or CombatAnimationMotionCommands.AttackHigh5
                or CombatAnimationMotionCommands.AttackMed5
                or CombatAnimationMotionCommands.AttackLow5
                or CombatAnimationMotionCommands.AttackHigh6
                or CombatAnimationMotionCommands.AttackMed6
                or CombatAnimationMotionCommands.AttackLow6 => CombatAnimationKind.CreatureAttack,

            CombatAnimationMotionCommands.CastSpell
                or CombatAnimationMotionCommands.UseMagicStaff
                or CombatAnimationMotionCommands.UseMagicWand => CombatAnimationKind.SpellCast,

            CombatAnimationMotionCommands.FallDown
                or CombatAnimationMotionCommands.Twitch1
                or CombatAnimationMotionCommands.Twitch2
                or CombatAnimationMotionCommands.Twitch3
                or CombatAnimationMotionCommands.Twitch4
                or CombatAnimationMotionCommands.StaggerBackward
                or CombatAnimationMotionCommands.StaggerForward
                or CombatAnimationMotionCommands.Sanctuary => CombatAnimationKind.HitReaction,

            MotionCommand.Dead => CombatAnimationKind.Death,

            _ => CombatAnimationKind.None,
        };
    }
}

public readonly record struct CombatAnimationPlan(
    CombatAnimationKind Kind,
    AnimationCommandRouteKind RouteKind,
    uint MotionCommand,
    float SpeedMod)
{
    public static CombatAnimationPlan None { get; } = new(
        CombatAnimationKind.None,
        AnimationCommandRouteKind.None,
        0u,
        0f);

    public bool HasMotion => Kind != CombatAnimationKind.None && MotionCommand != 0;
}

public enum CombatAnimationEvent
{
    CombatCommenceAttack,
    AttackDone,
    AttackerNotification,
    DefenderNotification,
    EvasionAttackerNotification,
    EvasionDefenderNotification,
    VictimNotification,
    KillerNotification,
}

public enum CombatAnimationKind
{
    None = 0,
    CombatStance,
    MeleeSwing,
    MissileAttack,
    CreatureAttack,
    SpellCast,
    HitReaction,
    Death,
}

internal static class CombatAnimationMotionCommands
{
    public const uint HandCombat            = (uint)Drw.HandCombat;
    public const uint SwordCombat           = (uint)Drw.SwordCombat;
    public const uint BowCombat             = (uint)Drw.BowCombat;
    public const uint SwordShieldCombat     = (uint)Drw.SwordShieldCombat;
    public const uint CrossbowCombat        = (uint)Drw.CrossbowCombat;
    public const uint SlingCombat           = (uint)Drw.SlingCombat;
    public const uint TwoHandedSwordCombat  = (uint)Drw.TwoHandedSwordCombat;
    public const uint TwoHandedStaffCombat  = (uint)Drw.TwoHandedStaffCombat;
    public const uint DualWieldCombat       = (uint)Drw.DualWieldCombat;
    public const uint ThrownWeaponCombat    = (uint)Drw.ThrownWeaponCombat;
    public const uint Magic                 = (uint)Drw.Magic;
    public const uint AtlatlCombat          = (uint)Drw.AtlatlCombat;
    public const uint ThrownShieldCombat    = (uint)Drw.ThrownShieldCombat;

    public const uint FallDown              = (uint)Drw.FallDown;
    public const uint Twitch1               = (uint)Drw.Twitch1;
    public const uint Twitch2               = (uint)Drw.Twitch2;
    public const uint Twitch3               = (uint)Drw.Twitch3;
    public const uint Twitch4               = (uint)Drw.Twitch4;
    public const uint StaggerBackward       = (uint)Drw.StaggerBackward;
    public const uint StaggerForward        = (uint)Drw.StaggerForward;
    public const uint Sanctuary             = (uint)Drw.Sanctuary;

    public const uint ThrustMed             = (uint)Drw.ThrustMed;
    public const uint ThrustLow             = (uint)Drw.ThrustLow;
    public const uint ThrustHigh            = (uint)Drw.ThrustHigh;
    public const uint SlashHigh             = (uint)Drw.SlashHigh;
    public const uint SlashMed              = (uint)Drw.SlashMed;
    public const uint SlashLow              = (uint)Drw.SlashLow;
    public const uint BackhandHigh          = (uint)Drw.BackhandHigh;
    public const uint BackhandMed           = (uint)Drw.BackhandMed;
    public const uint BackhandLow           = (uint)Drw.BackhandLow;

    public const uint Shoot                 = (uint)Drw.Shoot;
    public const uint MissileAttack1        = (uint)Drw.MissileAttack1;
    public const uint MissileAttack2        = (uint)Drw.MissileAttack2;
    public const uint MissileAttack3        = (uint)Drw.MissileAttack3;
    public const uint Reload                = (uint)Drw.Reload;

    public const uint AttackHigh1           = (uint)Drw.AttackHigh1;
    public const uint AttackMed1            = (uint)Drw.AttackMed1;
    public const uint AttackLow1            = (uint)Drw.AttackLow1;
    public const uint AttackHigh2           = (uint)Drw.AttackHigh2;
    public const uint AttackMed2            = (uint)Drw.AttackMed2;
    public const uint AttackLow2            = (uint)Drw.AttackLow2;
    public const uint AttackHigh3           = (uint)Drw.AttackHigh3;
    public const uint AttackMed3            = (uint)Drw.AttackMed3;
    public const uint AttackLow3            = (uint)Drw.AttackLow3;
    public const uint AttackHigh4           = (uint)Drw.AttackHigh4;
    public const uint AttackMed4            = (uint)Drw.AttackMed4;
    public const uint AttackLow4            = (uint)Drw.AttackLow4;
    public const uint AttackHigh5           = (uint)Drw.AttackHigh5;
    public const uint AttackMed5            = (uint)Drw.AttackMed5;
    public const uint AttackLow5            = (uint)Drw.AttackLow5;
    public const uint AttackHigh6           = (uint)Drw.AttackHigh6;
    public const uint AttackMed6            = (uint)Drw.AttackMed6;
    public const uint AttackLow6            = (uint)Drw.AttackLow6;

    public const uint CastSpell             = (uint)Drw.CastSpell;
    public const uint UseMagicStaff         = (uint)Drw.UseMagicStaff;
    public const uint UseMagicWand          = (uint)Drw.UseMagicWand;

    public const uint DoubleSlashLow        = (uint)Drw.DoubleSlashLow;
    public const uint DoubleSlashMed        = (uint)Drw.DoubleSlashMed;
    public const uint DoubleSlashHigh       = (uint)Drw.DoubleSlashHigh;
    public const uint TripleSlashLow        = (uint)Drw.TripleSlashLow;
    public const uint TripleSlashMed        = (uint)Drw.TripleSlashMed;
    public const uint TripleSlashHigh       = (uint)Drw.TripleSlashHigh;
    public const uint DoubleThrustLow       = (uint)Drw.DoubleThrustLow;
    public const uint DoubleThrustMed       = (uint)Drw.DoubleThrustMed;
    public const uint DoubleThrustHigh      = (uint)Drw.DoubleThrustHigh;
    public const uint TripleThrustLow       = (uint)Drw.TripleThrustLow;
    public const uint TripleThrustMed       = (uint)Drw.TripleThrustMed;
    public const uint TripleThrustHigh      = (uint)Drw.TripleThrustHigh;

    public const uint OffhandSlashHigh      = (uint)Drw.OffhandSlashHigh;
    public const uint OffhandSlashMed       = (uint)Drw.OffhandSlashMed;
    public const uint OffhandSlashLow       = (uint)Drw.OffhandSlashLow;
    public const uint OffhandThrustHigh     = (uint)Drw.OffhandThrustHigh;
    public const uint OffhandThrustMed      = (uint)Drw.OffhandThrustMed;
    public const uint OffhandThrustLow      = (uint)Drw.OffhandThrustLow;
    public const uint OffhandDoubleSlashLow = (uint)Drw.OffhandDoubleSlashLow;
    public const uint OffhandDoubleSlashMed = (uint)Drw.OffhandDoubleSlashMed;
    public const uint OffhandDoubleSlashHigh = (uint)Drw.OffhandDoubleSlashHigh;
    public const uint OffhandTripleSlashLow = (uint)Drw.OffhandTripleSlashLow;
    public const uint OffhandTripleSlashMed = (uint)Drw.OffhandTripleSlashMed;
    public const uint OffhandTripleSlashHigh = (uint)Drw.OffhandTripleSlashHigh;
    public const uint OffhandDoubleThrustLow = (uint)Drw.OffhandDoubleThrustLow;
    public const uint OffhandDoubleThrustMed = (uint)Drw.OffhandDoubleThrustMed;
    public const uint OffhandDoubleThrustHigh = (uint)Drw.OffhandDoubleThrustHigh;
    public const uint OffhandTripleThrustLow = (uint)Drw.OffhandTripleThrustLow;
    public const uint OffhandTripleThrustMed = (uint)Drw.OffhandTripleThrustMed;
    public const uint OffhandTripleThrustHigh = (uint)Drw.OffhandTripleThrustHigh;
    public const uint OffhandKick           = (uint)Drw.OffhandKick;

    public const uint PunchFastHigh         = (uint)Drw.PunchFastHigh;
    public const uint PunchFastMed          = (uint)Drw.PunchFastMed;
    public const uint PunchFastLow          = (uint)Drw.PunchFastLow;
    public const uint PunchSlowHigh         = (uint)Drw.PunchSlowHigh;
    public const uint PunchSlowMed          = (uint)Drw.PunchSlowMed;
    public const uint PunchSlowLow          = (uint)Drw.PunchSlowLow;
    public const uint OffhandPunchFastHigh  = (uint)Drw.OffhandPunchFastHigh;
    public const uint OffhandPunchFastMed   = (uint)Drw.OffhandPunchFastMed;
    public const uint OffhandPunchFastLow   = (uint)Drw.OffhandPunchFastLow;
    public const uint OffhandPunchSlowHigh  = (uint)Drw.OffhandPunchSlowHigh;
    public const uint OffhandPunchSlowMed   = (uint)Drw.OffhandPunchSlowMed;
    public const uint OffhandPunchSlowLow   = (uint)Drw.OffhandPunchSlowLow;
}
