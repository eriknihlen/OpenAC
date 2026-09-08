namespace AcDream.Core.CharGen;

public enum ChargenSkillAdvancementClass : uint
{
    Inactive = 0,
    Untrained = 1,
    Trained = 2,
    Specialized = 3,
}

public readonly record struct ChargenSkillCost(uint SkillId, int NormalCost, int PrimaryCost);

public readonly record struct ChargenSkillFormula(
    int AdditiveBonus,
    int Attribute1Multiplier,
    int Attribute2Multiplier,
    int Divisor,
    uint Attribute1,
    uint Attribute2);

public readonly record struct ChargenSkillDetail(
    uint SkillId,
    uint MinLevel,
    string Description,
    ChargenSkillFormula Formula);

public sealed class ChargenSkillAdvancementSet
{
    public const int SlotCount = 55;

    private readonly ChargenSkillAdvancementClass[] _slots = new ChargenSkillAdvancementClass[SlotCount];

    public ChargenSkillAdvancementClass this[uint skillId]
    {
        get => skillId >= 1 && skillId < SlotCount
            ? _slots[skillId]
            : ChargenSkillAdvancementClass.Inactive;
        set
        {
            if (skillId < 1 || skillId >= SlotCount)
                throw new ArgumentOutOfRangeException(
                    nameof(skillId),
                    skillId,
                    $"Skill id must be in 1..{SlotCount - 1}.");
            _slots[skillId] = value;
        }
    }

    public IReadOnlyList<uint> ToWireClasses()
    {
        var wire = new uint[SlotCount];
        for (int i = 0; i < SlotCount; i++)
            wire[i] = (uint)_slots[i];
        return wire;
    }
}
