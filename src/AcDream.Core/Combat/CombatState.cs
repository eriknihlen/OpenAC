using System;
using System.Collections.Concurrent;

namespace AcDream.Core.Combat;

public sealed class CombatState
{
    private readonly ConcurrentDictionary<uint, float> _healthByGuid = new();

    public CombatMode CurrentMode { get; private set; } = CombatMode.NonCombat;

    public event Action<uint /*guid*/, float /*percent*/>? HealthChanged;

    /// <summary>You (the player) got hit for some damage.</summary>
    public event Action<DamageIncoming>? DamageTaken;

    /// <summary>You (the player) dealt some damage.</summary>
    public event Action<DamageDealt>? DamageDealtAccepted;

    /// <summary>You (the player) evaded an incoming hit.</summary>
    public event Action<string>? EvadedIncoming;

    /// <summary>The target evaded your hit.</summary>
    public event Action<string>? MissedOutgoing;

    public event Action<uint /*attackSeq*/, uint /*weenieError*/>? AttackDone;

    /// <summary>The server accepted the attack and the power bar/animation can begin.</summary>
    public event Action? AttackCommenced;

    public event Action<CombatMode>? CombatModeChanged;

    public event Action<string /*victimName*/, uint /*victimGuid*/>? KillLanded;

    public readonly record struct DamageIncoming(
        string AttackerName,
        uint AttackerGuid,
        uint DamageType,
        uint Damage,
        uint HitQuadrant,
        bool Critical,
        uint AttackType,
        double DamagePercent = 0.0,
        ulong AttackConditions = 0ul);

    public readonly record struct DamageDealt(
        string DefenderName,
        uint DamageType,
        uint Damage,
        double DamagePercent,
        bool Critical = false,
        ulong AttackConditions = 0ul);

    /// <summary>Retrieve last known health percent for a guid, or 1.0 if unknown.</summary>
    public float GetHealthPercent(uint guid) =>
        _healthByGuid.TryGetValue(guid, out var pct) ? pct : 1f;

    public bool HasHealth(uint guid) => _healthByGuid.ContainsKey(guid);

    public int TrackedTargetCount => _healthByGuid.Count;

    // ── Inbound handlers (wired from WorldSession.GameEvents) ────────────────

    public void OnUpdateHealth(uint targetGuid, float healthPercent)
    {
        _healthByGuid[targetGuid] = healthPercent;
        HealthChanged?.Invoke(targetGuid, healthPercent);
    }

    public void SetCombatMode(CombatMode mode)
    {
        if (CurrentMode == mode)
            return;

        CurrentMode = mode;
        CombatModeChanged?.Invoke(mode);
    }

    public void OnVictimNotification(
        string attackerName, uint attackerGuid, uint damageType, uint damage,
        uint hitQuadrant, uint critical, uint attackType,
        double damagePercent = 0.0, ulong attackConditions = 0ul)
    {
        DamageTaken?.Invoke(new DamageIncoming(
            attackerName, attackerGuid, damageType, damage, hitQuadrant,
            critical != 0, attackType, damagePercent, attackConditions));
    }

    public void OnDefenderNotification(
        string attackerName, uint attackerGuid, uint damageType, uint damage,
        uint hitQuadrant, uint critical,
        double damagePercent = 0.0, ulong attackConditions = 0ul)
    {
        DamageTaken?.Invoke(new DamageIncoming(
            attackerName, attackerGuid, damageType, damage, hitQuadrant,
            critical != 0, 0, damagePercent, attackConditions));
    }

    public void OnAttackerNotification(
        string defenderName, uint damageType, uint damage, double damagePercent,
        uint critical = 0u, ulong attackConditions = 0ul)
    {
        DamageDealtAccepted?.Invoke(new DamageDealt(
            defenderName, damageType, damage, damagePercent,
            critical != 0, attackConditions));
    }

    public void OnEvasionAttackerNotification(string defenderName)
        => MissedOutgoing?.Invoke(defenderName);

    public void OnKillerNotification(string victimName, uint victimGuid)
        => KillLanded?.Invoke(victimName, victimGuid);

    public void OnEvasionDefenderNotification(string attackerName)
        => EvadedIncoming?.Invoke(attackerName);

    public void OnAttackDone(uint attackSequence, uint weenieError)
        => AttackDone?.Invoke(attackSequence, weenieError);

    public void OnCombatCommenceAttack()
        => AttackCommenced?.Invoke();

    public void Clear()
    {
        _healthByGuid.Clear();
        CurrentMode = CombatMode.NonCombat;

        Action<CombatMode>? listeners = CombatModeChanged;
        if (listeners is null)
            return;

        List<Exception>? failures = null;
        foreach (Action<CombatMode> listener in listeners.GetInvocationList())
        {
            try { listener(CombatMode.NonCombat); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException(
                "One or more combat reset observers failed.",
                failures);
    }
}
