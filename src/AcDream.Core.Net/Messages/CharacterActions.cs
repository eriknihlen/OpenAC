using System.Buffers.Binary;

namespace AcDream.Core.Net.Messages;

public static class CharacterActions
{
    public const uint GameActionEnvelope = 0xF7B1u;

    public const uint RaiseAttributeOpcode = 0x0045u;  // u32 attr, u32 xpSpent
    public const uint RaiseVitalOpcode     = 0x0044u;  // u32 vital, u32 xpSpent
    public const uint RaiseSkillOpcode     = 0x0046u;  // u32 skillId, u32 xpSpent
    public const uint TrainSkillOpcode     = 0x0047u;
    public const uint ChangeCombatModeOpcode = 0x0053u;

    [Flags]
    public enum CombatMode : uint
    {
        Undef = 0,
        NonCombat = 0x01,
        Melee = 0x02,
        Missile = 0x04,
        Magic = 0x08,

        ValidCombat = NonCombat | Melee | Missile | Magic,
        CombatCombat = Melee | Missile | Magic,
    }

    /// <summary>Spend XP to raise an attribute (Strength, Endurance, etc).</summary>
    public static byte[] BuildRaiseAttribute(uint seq, uint attrId, ulong xpSpent)
        => BuildAttrOrVital(seq, RaiseAttributeOpcode, attrId, xpSpent);

    /// <summary>Spend XP to raise a vital (Health, Stamina, Mana).</summary>
    public static byte[] BuildRaiseVital(uint seq, uint vitalId, ulong xpSpent)
        => BuildAttrOrVital(seq, RaiseVitalOpcode, vitalId, xpSpent);

    /// <summary>Spend XP to raise a skill.</summary>
    public static byte[] BuildRaiseSkill(uint seq, uint skillId, ulong xpSpent)
        => BuildAttrOrVital(seq, RaiseSkillOpcode, skillId, xpSpent);

    public static byte[] BuildTrainSkill(uint seq, uint skillId, uint credits)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  TrainSkillOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), skillId);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), credits);
        return body;
    }

    public static byte[] BuildChangeCombatMode(uint seq, CombatMode mode)
    {
        byte[] body = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  ChangeCombatModeOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), (uint)mode);
        return body;
    }

    private static byte[] BuildAttrOrVital(uint seq, uint sub, uint id, ulong xp)
    {
        byte[] body = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(body,            GameActionEnvelope);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),  seq);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),  sub);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(12), id);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(16), (uint)xp);
        return body;
    }
}
