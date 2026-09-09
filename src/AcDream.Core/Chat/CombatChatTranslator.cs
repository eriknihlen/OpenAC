using System;
using AcDream.Core.Combat;

namespace AcDream.Core.Chat;

public sealed class CombatChatTranslator : IDisposable
{
    private readonly CombatState _combat;
    private readonly ChatLog _chat;

    private readonly Action<CombatState.DamageDealt> _onDealt;
    private readonly Action<CombatState.DamageIncoming> _onTaken;
    private readonly Action<string> _onMissed;
    private readonly Action<string> _onEvaded;

    private bool _disposed;

    public CombatChatTranslator(
        CombatState combat,
        ChatLog chat,
        Func<bool>? accepting = null)
    {
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));

        _onDealt = value =>
        {
            if (accepting?.Invoke() != false) HandleDamageDealt(value);
        };
        _onTaken = value =>
        {
            if (accepting?.Invoke() != false) HandleDamageTaken(value);
        };
        _onMissed = value =>
        {
            if (accepting?.Invoke() != false) HandleMissedOutgoing(value);
        };
        _onEvaded = value =>
        {
            if (accepting?.Invoke() != false) HandleEvadedIncoming(value);
        };

        _combat.DamageDealtAccepted += _onDealt;
        _combat.DamageTaken += _onTaken;
        _combat.MissedOutgoing += _onMissed;
        _combat.EvadedIncoming += _onEvaded;

    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _combat.DamageDealtAccepted -= _onDealt;
        _combat.DamageTaken -= _onTaken;
        _combat.MissedOutgoing -= _onMissed;
        _combat.EvadedIncoming -= _onEvaded;
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private void HandleDamageDealt(CombatState.DamageDealt e)
    {
        string line = CombatNotificationText.AttackerLine(
            e.DefenderName, e.DamageType, e.DamagePercent,
            e.Damage, e.Critical, e.AttackConditions);
        _chat.OnCombatLine(line, logTextType: (uint)RetailLogTextType.CombatSelf, kind: CombatLineKind.Info);
    }

    private void HandleDamageTaken(CombatState.DamageIncoming e)
    {
        string line = CombatNotificationText.DefenderLine(
            e.AttackerName, e.DamageType, e.DamagePercent, e.Damage,
            unchecked((int)e.HitQuadrant), e.Critical, e.AttackConditions);
        _chat.OnCombatLine(line, logTextType: (uint)RetailLogTextType.CombatEnemy, kind: CombatLineKind.Warning);
    }

    private void HandleMissedOutgoing(string defenderName)
    {
        _chat.OnCombatLine(
            CombatNotificationText.EvasionAttackerLine(defenderName),
            logTextType: (uint)RetailLogTextType.CombatSelf, kind: CombatLineKind.Info);
    }

    private void HandleEvadedIncoming(string attackerName)
    {
        _chat.OnCombatLine(
            CombatNotificationText.EvasionDefenderLine(attackerName),
            logTextType: (uint)RetailLogTextType.CombatEnemy, kind: CombatLineKind.Info);
    }
}
