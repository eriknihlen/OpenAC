using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CombatEventTests
{
    [Fact]
    public void AttackTargetRequest_BuildMelee_EmitsRetailWireBytes()
    {
        byte[] body = AttackTargetRequest.BuildMelee(
            gameActionSequence: 3,
            targetGuid: 0x12345678u,
            attackHeight: 2,
            powerLevel: 0.75f);

        Assert.Equal(24, body.Length);
        Assert.Equal(AttackTargetRequest.GameActionEnvelope,
            BinaryPrimitives.ReadUInt32LittleEndian(body));
        Assert.Equal(3u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4)));
        Assert.Equal(AttackTargetRequest.TargetedMeleeAttackOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x12345678u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(2u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(0.75f,
            BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(20)), 4);
    }

    [Fact]
    public void AttackTargetRequest_BuildMissile_EmitsRetailWireBytes()
    {
        byte[] body = AttackTargetRequest.BuildMissile(
            gameActionSequence: 4,
            targetGuid: 0x87654321u,
            attackHeight: 1,
            accuracyLevel: 0.5f);

        Assert.Equal(24, body.Length);
        Assert.Equal(AttackTargetRequest.TargetedMissileAttackOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(0x87654321u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(1u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
        Assert.Equal(0.5f,
            BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(20)), 4);
    }

    [Fact]
    public void AttackTargetRequest_BuildCancel_HasNoPayload()
    {
        byte[] body = AttackTargetRequest.BuildCancel(gameActionSequence: 5);

        Assert.Equal(12, body.Length);
        Assert.Equal(AttackTargetRequest.CancelAttackOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void ParseAttackDone_PacketFixture_ReadsErrorCode()
    {
        var env = ParseFixture("B0F700000000000000000000A701000036000000");

        Assert.Equal(GameEventType.AttackDone, env.EventType);
        var parsed = GameEvents.ParseAttackDone(env.Payload.Span);

        Assert.NotNull(parsed);
        Assert.Equal(0u, parsed!.Value.AttackSequence);
        Assert.Equal(0x36u, parsed.Value.WeenieError);
    }

    [Fact]
    public void ParseAttackerNotification_PacketFixture_ReadsDamageAndConditions()
    {
        var env = ParseFixture("B0F700000000000001000000B10100000E0044727564676520526176656E657201000000000000000000D03F25000000010000000600000000000000");

        var parsed = GameEvents.ParseAttackerNotification(env.Payload.Span);

        Assert.NotNull(parsed);
        Assert.Equal("Drudge Ravener", parsed!.Value.DefenderName);
        Assert.Equal(1u, parsed.Value.DamageType);
        Assert.Equal(0.25, parsed.Value.HealthPercent, 6);
        Assert.Equal(37u, parsed.Value.Damage);
        Assert.Equal(1u, parsed.Value.Critical);
        Assert.Equal(6ul, parsed.Value.AttackConditions);
    }

    [Fact]
    public void ParseDefenderNotification_PacketFixture_ReadsDamageAndHitQuadrant()
    {
        var env = ParseFixture("B0F700000000000002000000B20100000A0042616E6465726C696E6710000000000000000000C03F1200000001000000000000000800000000000000");

        var parsed = GameEvents.ParseDefenderNotification(env.Payload.Span);

        Assert.NotNull(parsed);
        Assert.Equal("Banderling", parsed!.Value.AttackerName);
        Assert.Equal(0x10u, parsed.Value.DamageType);
        Assert.Equal(0.125, parsed.Value.HealthPercent, 6);
        Assert.Equal(18u, parsed.Value.Damage);
        Assert.Equal(1u, parsed.Value.HitQuadrant);
        Assert.Equal(0u, parsed.Value.Critical);
        Assert.Equal(8ul, parsed.Value.AttackConditions);
    }

    [Fact]
    public void ParseEvasionNotifications_PacketFixtures_ReadNames()
    {
        var attacker = ParseFixture("B0F700000000000003000000B301000008004D6F7373776172740000");
        var defender = ParseFixture("B0F700000000000004000000B401000004004D6974650000");

        Assert.Equal("Mosswart", GameEvents.ParseEvasionAttackerNotification(attacker.Payload.Span));
        Assert.Equal("Mite", GameEvents.ParseEvasionDefenderNotification(defender.Payload.Span));
    }

    [Fact]
    public void ParseCombatCommenceAttack_PacketFixture_RecognizesEvent()
    {
        var env = ParseFixture("B0F700000000000005000000B8010000");

        Assert.Equal(GameEventType.CombatCommenceAttack, env.EventType);
        Assert.True(GameEvents.ParseCombatCommenceAttack(env.Payload.Span));
    }

    [Fact]
    public void ParseDeathNotifications_PacketFixtures_ReadMessages()
    {
        var victim = ParseFixture("B0F700000000000006000000AC0100000E00596F752068617665206469656421");
        var killer = ParseFixture("B0F700000000000007000000AD0100001600596F75206B696C6C6564207468652064727564676521");

        Assert.Equal("You have died!", GameEvents.ParseVictimNotification(victim.Payload.Span)?.DeathMessage);
        Assert.Equal("You killed the drudge!", GameEvents.ParseKillerNotification(killer.Payload.Span)?.DeathMessage);
    }

    private static GameEventEnvelope ParseFixture(string hex)
    {
        byte[] body = Convert.FromHexString(hex);
        var env = GameEventEnvelope.TryParse(body);
        Assert.NotNull(env);
        return env.Value;
    }
}
