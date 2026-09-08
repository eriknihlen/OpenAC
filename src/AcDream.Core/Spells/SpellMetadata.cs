using System.Collections.Generic;

namespace AcDream.Core.Spells;

public sealed record SpellMetadata(
    uint   SpellId,
    string Name,
    string School,
    uint   Family,
    uint   IconId,
    string SpellWords,
    float  Duration,
    int    ManaCost,
    bool   IsDebuff,
    bool   IsFellowship,
    string Description,
    int    SortKey,
    int    Difficulty,
    uint   Flags,
    int    Generation,
    bool   IsFastWindup,
    bool   IsOffensive,
    bool   IsUntargeted,
    float  Speed,
    uint   CasterEffect,
    uint   TargetEffect,
    uint   TargetMask,
    int    SpellType)
{
    public MagicSchool SchoolId { get; init; }
    public IReadOnlyList<uint> FormulaComponents { get; init; } = [];
    public uint FormulaVersion { get; init; }
    public float ComponentLoss { get; init; }
    public float BaseRangeConstant { get; init; }
    public float BaseRangeModifier { get; init; }
    public float SpellEconomyModifier { get; init; }
    public uint FizzleEffect { get; init; }
    public double RecoveryInterval { get; init; }
    public float RecoveryAmount { get; init; }
    public uint NonComponentTargetType { get; init; }
    public uint FormulaTargetType { get; init; }
    public uint ManaModifier { get; init; }
    public float DegradeModifier { get; init; }
    public float DegradeLimit { get; init; }
    public double PortalLifetime { get; init; }

    public bool IsSelfTargeted => (Flags & (uint)SpellFlags.SelfTargeted) != 0;
    public bool IsBeneficial => (Flags & (uint)SpellFlags.Beneficial) != 0;
    public bool IsProjectile => (Flags & (uint)SpellFlags.Projectile) != 0;
}
