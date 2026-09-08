using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
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
    private readonly Action<string, uint> _onKill;

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
        _onKill = (name, guid) =>
        {
            if (accepting?.Invoke() != false) HandleKillLanded(name, guid);
        };

        _combat.DamageDealtAccepted += _onDealt;
        _combat.DamageTaken += _onTaken;
        _combat.MissedOutgoing += _onMissed;
        _combat.EvadedIncoming += _onEvaded;
        _combat.KillLanded += _onKill;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _combat.DamageDealtAccepted -= _onDealt;
        _combat.DamageTaken -= _onTaken;
        _combat.MissedOutgoing -= _onMissed;
        _combat.EvadedIncoming -= _onEvaded;
        _combat.KillLanded -= _onKill;
    }

    // ── Event handlers ──────────────────────────────────────────────────────

    private void HandleDamageDealt(CombatState.DamageDealt e)
    {
        var line = string.Concat(
            "You hit ", e.DefenderName,
            " for ", e.Damage.ToString(CultureInfo.InvariantCulture),
            " ", FormatDamageType(e.DamageType),
            " damage (", FormatPercent(e.DamagePercent), ").",
            "",
            FormatAttackConditionsSuffix(0));
        _chat.OnCombatLine(line, logTextType: (uint)RetailLogTextType.CombatSelf, kind: CombatLineKind.Info);
    }

    private void HandleDamageTaken(CombatState.DamageIncoming e)
    {
        var sb = new StringBuilder();
        sb.Append(e.AttackerName);
        sb.Append(" hit you for ");
        sb.Append(e.Damage.ToString(CultureInfo.InvariantCulture));
        sb.Append(' ');
        sb.Append(FormatDamageType(e.DamageType));
        sb.Append(" damage to your ");
        sb.Append(FormatDamageLocation(e.HitQuadrant));
        sb.Append('.');
        if (e.Critical) sb.Append(" Critical hit.");
        sb.Append(FormatAttackConditionsSuffix(0));
        _chat.OnCombatLine(sb.ToString(), logTextType: (uint)RetailLogTextType.CombatEnemy, kind: CombatLineKind.Warning);
    }

    private void HandleMissedOutgoing(string defenderName)
    {
        _chat.OnCombatLine($"{defenderName} evaded your attack.", logTextType: (uint)RetailLogTextType.CombatSelf, kind: CombatLineKind.Info);
    }

    private void HandleEvadedIncoming(string attackerName)
    {
        _chat.OnCombatLine($"You evaded {attackerName}'s attack.", logTextType: (uint)RetailLogTextType.CombatEnemy, kind: CombatLineKind.Info);
    }

    private void HandleKillLanded(string victimName, uint victimGuid)
    {
        _chat.OnCombatLine($"You killed {victimName}.", logTextType: 0x00u, kind: CombatLineKind.Info);
    }


    public static string FormatDamageType(uint damageType)
    {
        var names = new List<string>(4);
        if ((damageType & 0x1u) != 0) names.Add("slashing");
        if ((damageType & 0x2u) != 0) names.Add("piercing");
        if ((damageType & 0x4u) != 0) names.Add("bludgeoning");
        if ((damageType & 0x8u) != 0) names.Add("cold");
        if ((damageType & 0x10u) != 0) names.Add("fire");
        if ((damageType & 0x20u) != 0) names.Add("acid");
        if ((damageType & 0x40u) != 0) names.Add("electric");
        if ((damageType & 0x80u) != 0) names.Add("health");
        if ((damageType & 0x100u) != 0) names.Add("stamina");
        if ((damageType & 0x200u) != 0) names.Add("mana");
        if ((damageType & 0x400u) != 0) names.Add("nether");
        if ((damageType & 0x10000000u) != 0) names.Add("base");
        return names.Count == 0 ? "unknown" : string.Join("/", names);
    }

    public static string FormatDamageLocation(uint location) => location switch
    {
        0 => "head",
        1 => "chest",
        2 => "abdomen",
        3 => "upper arm",
        4 => "lower arm",
        5 => "hand",
        6 => "upper leg",
        7 => "lower leg",
        8 => "foot",
        _ => "body",
    };

    public static string FormatPercent(float fraction)
        => (fraction * 100f).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    public static string FormatAttackConditionsSuffix(uint attackConditions)
    {
        if (attackConditions == 0) return string.Empty;
        var names = new List<string>(2);
        if ((attackConditions & 0x1u) != 0) names.Add("Critical Protection Augmentation");
        if ((attackConditions & 0x2u) != 0) names.Add("Recklessness");
        if ((attackConditions & 0x4u) != 0) names.Add("Sneak Attack");
        if ((attackConditions & 0x8u) != 0) names.Add("Overpower");
        if (names.Count == 0) return string.Empty;
        return " [" + string.Join(", ", names) + "]";
    }
}
