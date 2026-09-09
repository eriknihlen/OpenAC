using AcDream.Core.Combat;
using AcDream.Core.Player;

namespace AcDream.UI.Abstractions.Panels.Vitals;

public sealed class VitalsVM
{
    private readonly CombatState _combat;
    private readonly LocalPlayerState? _local;
    private uint _localPlayerGuid;

    public VitalsVM(CombatState combat, LocalPlayerState? localPlayer = null)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _local = localPlayer;
        _localPlayerGuid = 0;
    }

    public void SetLocalPlayerGuid(uint guid) => _localPlayerGuid = guid;

    public float HealthPercent
        => _local?.HealthPercent ?? _combat.GetHealthPercent(_localPlayerGuid);

    public float? StaminaPercent => _local?.StaminaPercent;

    public float? ManaPercent => _local?.ManaPercent;

    // ── Absolute values for HUD overlays ──────────────────────────────────

    public uint? HealthCurrent => _local?.Get(LocalPlayerState.VitalKind.Health)?.Current;

    public uint? HealthMax => _local?.GetMaxApprox(LocalPlayerState.VitalKind.Health);

    public uint? StaminaCurrent => _local?.Get(LocalPlayerState.VitalKind.Stamina)?.Current;

    /// <summary>Max stamina including buffs + vitae.</summary>
    public uint? StaminaMax => _local?.GetMaxApprox(LocalPlayerState.VitalKind.Stamina);

    public uint? ManaCurrent => _local?.Get(LocalPlayerState.VitalKind.Mana)?.Current;

    /// <summary>Max mana including buffs + vitae.</summary>
    public uint? ManaMax => _local?.GetMaxApprox(LocalPlayerState.VitalKind.Mana);
}
