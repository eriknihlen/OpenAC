using DatReaderWriter.DBObjs;
using DatMotionCommand = DatReaderWriter.Enums.MotionCommand;
using DatMotionStance = DatReaderWriter.Enums.MotionStance;
using DatAttackHeight = DatReaderWriter.Enums.AttackHeight;
using DatAttackType = DatReaderWriter.Enums.AttackType;

namespace AcDream.Core.Combat;

public static class CombatManeuverSelector
{
    public const float DefaultSubdivision = 0.33f;
    public const float ThrustSlashSubdivision = 0.66f;

    public static CombatManeuverSelection SelectMotion(
        CombatTable table,
        DatMotionStance stance,
        DatAttackHeight attackHeight,
        DatAttackType attackType,
        float powerLevel,
        bool isThrustSlashWeapon = false)
    {
        var motions = FindMotions(table, stance, attackHeight, attackType);
        if (motions.Count == 0)
            return CombatManeuverSelection.None;

        float subdivision = isThrustSlashWeapon
            ? ThrustSlashSubdivision
            : DefaultSubdivision;

        var motion = motions.Count > 1 && powerLevel < subdivision
            ? motions[1]
            : motions[0];

        return new CombatManeuverSelection(
            Found: true,
            Motion: motion,
            Candidates: motions,
            EffectiveAttackType: attackType,
            Subdivision: subdivision);
    }

    public static IReadOnlyList<DatMotionCommand> FindMotions(
        CombatTable table,
        DatMotionStance stance,
        DatAttackHeight attackHeight,
        DatAttackType attackType)
    {
        var result = new List<DatMotionCommand>();

        foreach (var maneuver in table.CombatManeuvers)
        {
            if (maneuver.Style == stance
                && maneuver.AttackHeight == attackHeight
                && maneuver.AttackType == attackType)
            {
                result.Add(maneuver.Motion);
            }
        }

        return result;
    }
}

public readonly record struct CombatManeuverSelection(
    bool Found,
    DatMotionCommand Motion,
    IReadOnlyList<DatMotionCommand> Candidates,
    DatAttackType EffectiveAttackType,
    float Subdivision)
{
    public static CombatManeuverSelection None { get; } = new(
        Found: false,
        Motion: DatMotionCommand.Invalid,
        Candidates: Array.Empty<DatMotionCommand>(),
        EffectiveAttackType: DatAttackType.Undef,
        Subdivision: 0f);
}
