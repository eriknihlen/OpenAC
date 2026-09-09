using AcDream.Runtime.Gameplay;

namespace AcDream.App.Net;

internal sealed class LiveMovementStatsApplier(
    RuntimeLocalPlayerMovementState movement,
    RuntimeMovementSkillState skills,
    Action<string> log)
{
    private readonly RuntimeLocalPlayerMovementState _movement = movement
        ?? throw new ArgumentNullException(nameof(movement));
    private readonly RuntimeMovementSkillState _skills = skills
        ?? throw new ArgumentNullException(nameof(skills));
    private readonly Action<string> _log = log
        ?? throw new ArgumentNullException(nameof(log));
    private readonly StaminaExhaustionEdgeTracker _staminaExhaustion = new();

    public void Reset() => _staminaExhaustion.Reset();

    public RuntimeMovementStatsApplication Apply(string reason)
    {
        RuntimeMovementStatsApplication outcome =
            _movement.ApplyCharacterMovementStats(_skills);
        switch (outcome)
        {
            case RuntimeMovementStatsApplication.DroppedNoController:
            case RuntimeMovementStatsApplication.DroppedIncompleteSnapshot:
                // Byte-identical to the pre-F1 ApplyTo=false silent skip.
                return outcome;
            case RuntimeMovementStatsApplication.DroppedDisplacedController:
                _log(
                    $"player: dropped displaced movement {reason} — the "
                    + "Runtime movement controller is terminal");
                return outcome;
        }

        RuntimeMovementSkillSnapshot snapshot = _skills.Snapshot;
        if (_staminaExhaustion.Observe(snapshot.CurrentStamina)
            && outcome is RuntimeMovementStatsApplication.AppliedLive)
        {
            _movement.ReportExhaustion();
        }

        _log(
            $"player: applied server movement {reason} "
            + $"run={snapshot.RunSkill} jump={snapshot.JumpSkill} "
            + $"burden={snapshot.Burden:F2} stamina={snapshot.CurrentStamina}");
        return outcome;
    }
}
