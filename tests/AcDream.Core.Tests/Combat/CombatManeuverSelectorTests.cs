using AcDream.Core.Combat;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;
using DatAttackHeight = DatReaderWriter.Enums.AttackHeight;
using DatAttackType = DatReaderWriter.Enums.AttackType;
using DatMotionCommand = DatReaderWriter.Enums.MotionCommand;
using DatMotionStance = DatReaderWriter.Enums.MotionStance;
using Xunit;

namespace AcDream.Core.Tests.Combat;

public sealed class CombatManeuverSelectorTests
{
    [Fact]
    public void SelectMotion_UsesFirstEntryAtOrAboveSubdivision()
    {
        var table = MakeTable(
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.Medium,
                DatAttackType.Slash, DatMotionCommand.SlashMed),
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.Medium,
                DatAttackType.Slash, DatMotionCommand.BackhandMed));

        var atThreshold = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.Medium,
            DatAttackType.Slash,
            powerLevel: CombatManeuverSelector.DefaultSubdivision);

        var highPower = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.Medium,
            DatAttackType.Slash,
            powerLevel: 1f);

        Assert.Equal(DatMotionCommand.SlashMed, atThreshold.Motion);
        Assert.Equal(DatMotionCommand.SlashMed, highPower.Motion);
    }

    [Fact]
    public void SelectMotion_UsesSecondEntryBelowSubdivision()
    {
        var table = MakeTable(
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.Medium,
                DatAttackType.Slash, DatMotionCommand.SlashMed),
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.Medium,
                DatAttackType.Slash, DatMotionCommand.BackhandMed));

        var selection = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.Medium,
            DatAttackType.Slash,
            powerLevel: 0.2f);

        Assert.True(selection.Found);
        Assert.Equal(DatMotionCommand.BackhandMed, selection.Motion);
        Assert.Equal(DatAttackType.Slash, selection.EffectiveAttackType);
        Assert.Equal(2, selection.Candidates.Count);
    }

    [Fact]
    public void SelectMotion_ThrustSlashWeaponUsesTwoThirdsSubdivision()
    {
        var table = MakeTable(
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.High,
                DatAttackType.Slash, DatMotionCommand.SlashHigh),
            Entry(DatMotionStance.SwordCombat, DatAttackHeight.High,
                DatAttackType.Slash, DatMotionCommand.BackhandHigh));

        var normal = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.High,
            DatAttackType.Slash,
            powerLevel: 0.5f);

        var thrustSlash = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.High,
            DatAttackType.Slash,
            powerLevel: 0.5f,
            isThrustSlashWeapon: true);

        Assert.Equal(DatMotionCommand.SlashHigh, normal.Motion);
        Assert.Equal(DatMotionCommand.BackhandHigh, thrustSlash.Motion);
        Assert.Equal(CombatManeuverSelector.ThrustSlashSubdivision, thrustSlash.Subdivision);
    }

    [Fact]
    public void SelectMotion_MissingLookupReturnsNone()
    {
        var table = MakeTable(
            Entry(DatMotionStance.BowCombat, DatAttackHeight.High,
                DatAttackType.Punch, DatMotionCommand.Shoot));

        var selection = CombatManeuverSelector.SelectMotion(
            table,
            DatMotionStance.SwordCombat,
            DatAttackHeight.High,
            DatAttackType.Punch,
            powerLevel: 0.5f);

        Assert.Equal(CombatManeuverSelection.None, selection);
    }

    [Fact]
    public void FindMotions_PreservesRetailTableOrder()
    {
        var table = MakeTable(
            Entry(DatMotionStance.HandCombat, DatAttackHeight.Low,
                DatAttackType.Kick, DatMotionCommand.AttackLow1),
            Entry(DatMotionStance.HandCombat, DatAttackHeight.Low,
                DatAttackType.Kick, (DatMotionCommand)0x1000018Eu),
            Entry(DatMotionStance.HandCombat, DatAttackHeight.Low,
                DatAttackType.Punch, DatMotionCommand.AttackLow2));

        var motions = CombatManeuverSelector.FindMotions(
            table,
            DatMotionStance.HandCombat,
            DatAttackHeight.Low,
            DatAttackType.Kick);

        Assert.Equal(new[]
        {
            DatMotionCommand.AttackLow1,
            (DatMotionCommand)0x1000018Eu,
        }, motions);
    }

    private static CombatTable MakeTable(params CombatManeuver[] maneuvers)
    {
        var table = new CombatTable();
        table.CombatManeuvers.AddRange(maneuvers);
        return table;
    }

    private static CombatManeuver Entry(
        DatMotionStance stance,
        DatAttackHeight height,
        DatAttackType type,
        DatMotionCommand motion)
    {
        return new CombatManeuver
        {
            Style = stance,
            AttackHeight = height,
            AttackType = type,
            MinSkillLevel = 0,
            Motion = motion,
        };
    }
}
