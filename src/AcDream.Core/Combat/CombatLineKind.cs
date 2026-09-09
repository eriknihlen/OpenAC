namespace AcDream.Core.Combat;

public enum CombatLineKind
{
    /// <summary>Standard outgoing-damage / target-evaded line. Yellow-ish in the panel.</summary>
    Info,

    /// <summary>Incoming-damage line. Red-ish in the panel.</summary>
    Warning,

    /// <summary>Attack failure (AttackDone with a non-zero WeenieError). Deep red in the panel.</summary>
    Error,
}
