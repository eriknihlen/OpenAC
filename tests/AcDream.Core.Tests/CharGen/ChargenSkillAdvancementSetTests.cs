using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenSkillAdvancementSetTests
{
    [Fact]
    public void ToWireClasses_AlwaysProducesExactlyFiftyFiveEntries()
    {
        var set = new ChargenSkillAdvancementSet();

        Assert.Equal(55, set.ToWireClasses().Count);
        Assert.Equal(55, ChargenSkillAdvancementSet.SlotCount);
    }

    [Fact]
    public void DefaultState_EverySlotIsInactive()
    {
        var set = new ChargenSkillAdvancementSet();

        IReadOnlyList<uint> wire = set.ToWireClasses();
        Assert.All(wire, value => Assert.Equal(0u, value));
        Assert.Equal(ChargenSkillAdvancementClass.Inactive, set[1u]);
        Assert.Equal(ChargenSkillAdvancementClass.Inactive, set[54u]);
    }

    [Fact]
    public void Indexer_RoundTripsAssignedSkillState()
    {
        var set = new ChargenSkillAdvancementSet
        {
            [1u] = ChargenSkillAdvancementClass.Trained,
            [54u] = ChargenSkillAdvancementClass.Specialized,
        };

        Assert.Equal(ChargenSkillAdvancementClass.Trained, set[1u]);
        Assert.Equal(ChargenSkillAdvancementClass.Specialized, set[54u]);

        IReadOnlyList<uint> wire = set.ToWireClasses();
        Assert.Equal(0u, wire[0]); // reserved slot never assignable
        Assert.Equal((uint)ChargenSkillAdvancementClass.Trained, wire[1]);
        Assert.Equal((uint)ChargenSkillAdvancementClass.Specialized, wire[54]);
    }

    [Fact]
    public void Indexer_ReservedSlotZero_ReadsInactiveAndCannotBeSet()
    {
        var set = new ChargenSkillAdvancementSet();

        Assert.Equal(ChargenSkillAdvancementClass.Inactive, set[0u]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => set[0u] = ChargenSkillAdvancementClass.Trained);
    }

    [Theory]
    [InlineData(55u)]
    [InlineData(1000u)]
    public void Indexer_SetOutOfRange_Throws(uint skillId)
    {
        var set = new ChargenSkillAdvancementSet();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => set[skillId] = ChargenSkillAdvancementClass.Trained);
    }

    [Fact]
    public void Indexer_GetOutOfRange_ReadsInactiveWithoutThrowing()
    {
        var set = new ChargenSkillAdvancementSet();

        Assert.Equal(ChargenSkillAdvancementClass.Inactive, set[55u]);
        Assert.Equal(ChargenSkillAdvancementClass.Inactive, set[uint.MaxValue]);
    }
}
