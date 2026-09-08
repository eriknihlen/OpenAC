using AcDream.Core.Chat;
using AcDream.Core.Combat;
using Xunit;

namespace AcDream.Core.Tests.Chat;

public sealed class CombatChatTranslatorTests
{
    private static (ChatLog, CombatState, CombatChatTranslator) Setup()
    {
        var chat = new ChatLog();
        var combat = new CombatState();
        var t = new CombatChatTranslator(combat, chat);
        return (chat, combat, t);
    }

    [Fact]
    public void DamageDealt_FormatsHolthurgerAttackerTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        // damage_type 0x01 = SLASH → "slashing"
        combat.OnAttackerNotification(
            defenderName: "Mosswart Defiler", damageType: 0x01u,
            damage: 12u, damagePercent: 0.54f);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("You hit Mosswart Defiler for 12 slashing damage (54.0%).", entry.Text);
        Assert.Equal(0x16u, entry.LogTextType);
    }

    [Fact]
    public void DamageTaken_FormatsHolthurgerDefenderTemplate_AsWarning()
    {
        var (chat, combat, _) = Setup();
        combat.OnVictimNotification(
            attackerName: "Mosswart Stalker", attackerGuid: 0xA1u,
            damageType: 0x10u, damage: 7u,
            hitQuadrant: 1u, critical: 0u, attackType: 0u);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Warning, entry.CombatKind);
        Assert.Equal("Mosswart Stalker hit you for 7 fire damage to your chest.", entry.Text);
        Assert.Equal(0x15u, entry.LogTextType);
    }

    [Fact]
    public void DamageTaken_Critical_AppendsCriticalHitSuffix()
    {
        var (chat, combat, _) = Setup();
        combat.OnVictimNotification(
            attackerName: "Olthoi Soldier", attackerGuid: 0xB2u,
            damageType: 0x02u /*PIERCE*/, damage: 99u,
            hitQuadrant: 0u /*head*/, critical: 1u, attackType: 0u);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(
            "Olthoi Soldier hit you for 99 piercing damage to your head. Critical hit.",
            entry.Text);
    }

    [Fact]
    public void MissedOutgoing_FormatsEvasionAttackerTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        combat.OnEvasionAttackerNotification("Mosswart Sniper");

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("Mosswart Sniper evaded your attack.", entry.Text);
        Assert.Equal(0x16u, entry.LogTextType);
    }

    [Fact]
    public void EvadedIncoming_FormatsEvasionDefenderTemplate_AsInfo()
    {
        var (chat, combat, _) = Setup();
        combat.OnEvasionDefenderNotification("Drudge Slinker");

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("You evaded Drudge Slinker's attack.", entry.Text);
        Assert.Equal(0x15u, entry.LogTextType);
    }

    [Fact]
    public void AttackDone_NonZeroControlStatus_EmitsNothing()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackDone(attackSequence: 7, weenieError: 0x0036u);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void AttackDone_ZeroError_EmitsNothing()
    {
        var (chat, combat, _) = Setup();
        combat.OnAttackDone(attackSequence: 7, weenieError: 0u);

        Assert.Empty(chat.Snapshot());
    }

    [Fact]
    public void KillLanded_EmitsInfoKillerLine()
    {
        var (chat, combat, _) = Setup();
        combat.OnKillerNotification(victimName: "Phyntos Wasp", victimGuid: 0xCAFEu);

        var entry = Assert.Single(chat.Snapshot());
        Assert.Equal(ChatKind.Combat, entry.Kind);
        Assert.Equal(CombatLineKind.Info, entry.CombatKind);
        Assert.Equal("You killed Phyntos Wasp.", entry.Text);
        Assert.Equal(0x00u, entry.LogTextType);
    }

    [Fact]
    public void FormatDamageType_PerHolthurgerBitNames_SingleBits()
    {
        Assert.Equal("slashing",   CombatChatTranslator.FormatDamageType(0x01u));
        Assert.Equal("fire",       CombatChatTranslator.FormatDamageType(0x10u));
        Assert.Equal("nether",     CombatChatTranslator.FormatDamageType(0x400u));
        Assert.Equal("unknown",    CombatChatTranslator.FormatDamageType(0u));
    }

    [Fact]
    public void FormatDamageType_MultipleBits_JoinsWithSlash()
    {
        // Slash + Pierce → "slashing/piercing"
        Assert.Equal("slashing/piercing",
            CombatChatTranslator.FormatDamageType(0x01u | 0x02u));
    }

    [Fact]
    public void FormatDamageLocation_PerHolthurgerEnum()
    {
        Assert.Equal("head",       CombatChatTranslator.FormatDamageLocation(0));
        Assert.Equal("chest",      CombatChatTranslator.FormatDamageLocation(1));
        Assert.Equal("upper arm",  CombatChatTranslator.FormatDamageLocation(3));
        Assert.Equal("foot",       CombatChatTranslator.FormatDamageLocation(8));
        // Out-of-range → graceful "body" fallback (defensive).
        Assert.Equal("body",       CombatChatTranslator.FormatDamageLocation(99));
    }

    [Fact]
    public void Dispose_UnsubscribesFromAllEvents()
    {
        var (chat, combat, t) = Setup();
        t.Dispose();

        combat.OnAttackerNotification("X", 0x01u, 1u, 1.0f);
        combat.OnVictimNotification("X", 0u, 0x01u, 1u, 0u, 0u, 0u);
        combat.OnEvasionAttackerNotification("X");
        combat.OnEvasionDefenderNotification("X");
        combat.OnAttackDone(0u, 0xFFu);
        combat.OnKillerNotification("X", 0u);

        Assert.Empty(chat.Snapshot());
    }
}
