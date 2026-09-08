using AcDream.Core.Combat;
using AcDream.Core.Physics;
using Xunit;

namespace AcDream.Core.Tests.Combat;

public sealed class CombatAnimationPlannerTests
{
    [Theory]
    [InlineData(0x10000058u, CombatAnimationKind.MeleeSwing)]     // ThrustMed
    [InlineData(0x1000005Bu, CombatAnimationKind.MeleeSwing)]     // SlashHigh
    [InlineData(0x1000017Du, CombatAnimationKind.MeleeSwing)]     // OffhandTripleSlashMed (DRW)
    [InlineData(0x1000018Eu, CombatAnimationKind.CreatureAttack)] // AttackLow6 (DRW) — was mislabelled PunchFastLow under 2013 numbering
    [InlineData(0x10000061u, CombatAnimationKind.MissileAttack)]  // Shoot
    [InlineData(0x40000016u, CombatAnimationKind.MissileAttack)]
    [InlineData(0x10000062u, CombatAnimationKind.CreatureAttack)] // AttackHigh1
    [InlineData(0x1000018Bu, CombatAnimationKind.CreatureAttack)] // AttackLow5 (DRW)
    [InlineData(0x400000D3u, CombatAnimationKind.SpellCast)]
    [InlineData(0x400000E0u, CombatAnimationKind.SpellCast)]      // UseMagicStaff
    [InlineData(0x10000051u, CombatAnimationKind.HitReaction)]    // Twitch1
    [InlineData(0x10000055u, CombatAnimationKind.HitReaction)]    // StaggerBackward
    [InlineData(0x40000011u, CombatAnimationKind.Death)]          // Dead
    [InlineData(0x8000003Eu, CombatAnimationKind.CombatStance)]   // SwordCombat
    [InlineData(0x80000043u, CombatAnimationKind.CombatStance)]   // SlingCombat
    [InlineData(0x80000044u, CombatAnimationKind.CombatStance)]   // 2HandedSwordCombat
    public void ClassifyMotionCommand_RecognisesRetailCombatCommands(
        uint command,
        CombatAnimationKind expected)
    {
        Assert.Equal(expected, CombatAnimationPlanner.ClassifyMotionCommand(command));
    }

    [Theory]
    [InlineData(0x0170, 0x09000170u)]
    [InlineData(0x017D, 0x1000017Du)] // OffhandTripleSlashMed
    [InlineData(0x018B, 0x1000018Bu)] // AttackLow5
    [InlineData(0x018E, 0x1000018Eu)] // AttackLow6
    public void MotionCommandResolver_UsesNamedRetailLateCombatCommands(
        ushort wireCommand,
        uint expectedFullCommand)
    {
        Assert.Equal(expectedFullCommand, MotionCommandResolver.ReconstructFullCommand(wireCommand));
    }

    [Theory]
    [InlineData((ushort)0x0173, 0x10000173u, CombatAnimationKind.MeleeSwing)]     // OffhandSlashHigh
    [InlineData((ushort)0x0175, 0x10000175u, CombatAnimationKind.MeleeSwing)]     // OffhandSlashLow
    [InlineData((ushort)0x0176, 0x10000176u, CombatAnimationKind.MeleeSwing)]     // OffhandThrustHigh
    [InlineData((ushort)0x017E, 0x1000017Eu, CombatAnimationKind.MeleeSwing)]     // OffhandTripleSlashHigh
    [InlineData((ushort)0x0182, 0x10000182u, CombatAnimationKind.MeleeSwing)]     // OffhandTripleThrustLow
    [InlineData((ushort)0x0185, 0x10000185u, CombatAnimationKind.MeleeSwing)]     // OffhandKick
    [InlineData((ushort)0x0186, 0x10000186u, CombatAnimationKind.CreatureAttack)] // AttackHigh4
    [InlineData((ushort)0x0188, 0x10000188u, CombatAnimationKind.CreatureAttack)] // AttackLow4
    [InlineData((ushort)0x018C, 0x1000018Cu, CombatAnimationKind.CreatureAttack)] // AttackHigh6
    [InlineData((ushort)0x018E, 0x1000018Eu, CombatAnimationKind.CreatureAttack)] // AttackLow6
    [InlineData((ushort)0x018F, 0x1000018Fu, CombatAnimationKind.MeleeSwing)]     // PunchFastHigh
    [InlineData((ushort)0x0191, 0x10000191u, CombatAnimationKind.MeleeSwing)]     // PunchFastLow
    [InlineData((ushort)0x0197, 0x10000197u, CombatAnimationKind.MeleeSwing)]     // OffhandPunchFastLow
    [InlineData((ushort)0x019A, 0x1000019Au, CombatAnimationKind.MeleeSwing)]     // OffhandPunchSlowLow
    public void PlanFromWireCommand_LateCombatBlock_UsesAceDrwNumbering(
        ushort wireCommand,
        uint expectedFullCommand,
        CombatAnimationKind expectedKind)
    {
        var plan = CombatAnimationPlanner.PlanFromWireCommand(wireCommand);
        Assert.Equal(expectedFullCommand, plan.MotionCommand);
        Assert.Equal(expectedKind, plan.Kind);
    }

    [Fact]
    public void PlanFromWireCommand_Reload_UsesDrwSubStateValue()
    {
        var plan = CombatAnimationPlanner.PlanFromWireCommand(0x0016);

        Assert.Equal(0x40000016u, plan.MotionCommand);
        Assert.Equal(CombatAnimationKind.MissileAttack, plan.Kind);
        Assert.Equal(AnimationCommandRouteKind.SubState, plan.RouteKind);
    }

    [Fact]
    public void PlanFromWireCommand_Swing_IsActionOverlay()
    {
        var plan = CombatAnimationPlanner.PlanFromWireCommand(0x0058, speedMod: 1.25f);

        Assert.Equal(CombatAnimationKind.MeleeSwing, plan.Kind);
        Assert.Equal(AnimationCommandRouteKind.Action, plan.RouteKind);
        Assert.Equal(0x10000058u, plan.MotionCommand);
        Assert.Equal(1.25f, plan.SpeedMod);
        Assert.True(plan.HasMotion);
    }

    [Fact]
    public void PlanFromWireCommand_Dead_IsPersistentSubState()
    {
        var plan = CombatAnimationPlanner.PlanFromWireCommand(0x0011);

        Assert.Equal(CombatAnimationKind.Death, plan.Kind);
        Assert.Equal(AnimationCommandRouteKind.SubState, plan.RouteKind);
        Assert.Equal(MotionCommand.Dead, plan.MotionCommand);
    }

    [Fact]
    public void PlanFromWireCommand_Unknown_IsNone()
    {
        var plan = CombatAnimationPlanner.PlanFromWireCommand(0xFFFF);

        Assert.Equal(CombatAnimationPlan.None, plan);
        Assert.False(plan.HasMotion);
    }

    [Theory]
    [InlineData(CombatAnimationEvent.CombatCommenceAttack)]
    [InlineData(CombatAnimationEvent.AttackDone)]
    [InlineData(CombatAnimationEvent.AttackerNotification)]
    [InlineData(CombatAnimationEvent.DefenderNotification)]
    [InlineData(CombatAnimationEvent.EvasionAttackerNotification)]
    [InlineData(CombatAnimationEvent.EvasionDefenderNotification)]
    [InlineData(CombatAnimationEvent.VictimNotification)]
    [InlineData(CombatAnimationEvent.KillerNotification)]
    public void PlanForEvent_DoesNotInventAnimations(CombatAnimationEvent combatEvent)
    {
        Assert.Equal(CombatAnimationPlan.None, CombatAnimationPlanner.PlanForEvent(combatEvent));
    }
}
