using System;
using System.Buffers.Binary;
using AcDream.Core.Net.Messages;
using Xunit;

namespace AcDream.Core.Net.Tests.Messages;

public sealed class CharacterActionsTests
{
    [Fact]
    public void BuildRaiseAttribute_HasOpcode0x0045AndU32Xp()
    {
        byte[] body = CharacterActions.BuildRaiseAttribute(seq: 1, attrId: 5, xpSpent: 12345678);
        Assert.Equal(20, body.Length);
        Assert.Equal(CharacterActions.RaiseAttributeOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(5u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
        Assert.Equal(12345678u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(12345678u)]
    [InlineData(uint.MaxValue)]
    public void RaiseVitalAndSkill_AlsoWriteU32Xp(uint xp)
    {
        byte[] vital = CharacterActions.BuildRaiseVital(seq: 1, vitalId: 1, xpSpent: xp);
        byte[] skill = CharacterActions.BuildRaiseSkill(seq: 1, skillId: 8, xpSpent: xp);

        Assert.Equal(20, vital.Length);
        Assert.Equal(20, skill.Length);
        Assert.Equal(xp, BinaryPrimitives.ReadUInt32LittleEndian(vital.AsSpan(16)));
        Assert.Equal(xp, BinaryPrimitives.ReadUInt32LittleEndian(skill.AsSpan(16)));
    }

    [Fact]
    public void BuildRaiseVital_HasOpcode0x0044()
    {
        byte[] body = CharacterActions.BuildRaiseVital(seq: 1, vitalId: 1, xpSpent: 100);
        Assert.Equal(CharacterActions.RaiseVitalOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildRaiseSkill_HasOpcode0x0046()
    {
        byte[] body = CharacterActions.BuildRaiseSkill(seq: 1, skillId: 8, xpSpent: 500);
        Assert.Equal(CharacterActions.RaiseSkillOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
    }

    [Fact]
    public void BuildTrainSkill_U32CreditsNotU64()
    {
        byte[] body = CharacterActions.BuildTrainSkill(seq: 1, skillId: 8, credits: 4);
        Assert.Equal(20, body.Length);  // 12 + 4 + 4 vs 16 for 4+8
        Assert.Equal(CharacterActions.TrainSkillOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(4u,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(16)));
    }

    [Fact]
    public void BuildChangeCombatMode_EnumSerialises()
    {
        byte[] body = CharacterActions.BuildChangeCombatMode(
            seq: 1, CharacterActions.CombatMode.Melee);
        Assert.Equal(CharacterActions.ChangeCombatModeOpcode,
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)));
        Assert.Equal(2u,    // Melee = 2
            BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(12)));
    }

    [Fact]
    public void CombatMode_UsesRetailAceBitValues()
    {
        Assert.Equal(1u, (uint)CharacterActions.CombatMode.NonCombat);
        Assert.Equal(2u, (uint)CharacterActions.CombatMode.Melee);
        Assert.Equal(4u, (uint)CharacterActions.CombatMode.Missile);
        Assert.Equal(8u, (uint)CharacterActions.CombatMode.Magic);
    }
}
