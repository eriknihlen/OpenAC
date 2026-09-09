using AcDream.Core.CharGen;

namespace AcDream.Core.Tests.CharGen;

public sealed class ChargenOptionsTests
{
    private static ChargenHeritageOptions MakeHeritage(uint id, string name) => new(
        HeritageId: id,
        Name: name,
        IconId: 0x06000001u,
        SetupId: 0x02000001u,
        EnvironmentSetupId: 0x02000002u,
        AttributeCredits: 180u,
        SkillCredits: 100u,
        PrimaryStartAreaIndices: [0],
        SecondaryStartAreaIndices: [],
        SkillCostsBySkillId: new Dictionary<uint, ChargenSkillCost>(),
        Templates: [],
        GendersByKey: new Dictionary<int, ChargenGenderOptions>());

    [Fact]
    public void Empty_HasNoStarterAreasOrHeritages()
    {
        Assert.Empty(ChargenOptions.Empty.StarterAreas);
        Assert.Empty(ChargenOptions.Empty.HeritagesById);
    }

    [Fact]
    public void TryGetHeritage_FindsRegisteredHeritageById()
    {
        var aluvian = MakeHeritage(1u, "Aluvian");
        var options = new ChargenOptions(
            [],
            new Dictionary<uint, ChargenHeritageOptions> { [1u] = aluvian },
            new Dictionary<uint, ChargenSkillCost>());

        Assert.True(options.TryGetHeritage(1u, out ChargenHeritageOptions? found));
        Assert.Same(aluvian, found);
    }

    [Fact]
    public void TryGetHeritage_MissingIdReturnsFalse()
    {
        var options = ChargenOptions.Empty;

        Assert.False(options.TryGetHeritage(1u, out _));
    }

    [Fact]
    public void TryGetStarterArea_ResolvesByIndexAndRejectsOutOfRange()
    {
        var area = new ChargenStarterArea(0, "Holtburg", []);
        var options = new ChargenOptions(
            [area],
            new Dictionary<uint, ChargenHeritageOptions>(),
            new Dictionary<uint, ChargenSkillCost>());

        Assert.True(options.TryGetStarterArea(0, out ChargenStarterArea? found));
        Assert.Same(area, found);
        Assert.False(options.TryGetStarterArea(1, out _));
        Assert.False(options.TryGetStarterArea(-1, out _));
    }

    [Theory]
    [InlineData(1u, false)]  // Aluvian
    [InlineData(11u, false)] // Undead
    [InlineData(12u, true)]  // Olthoi
    [InlineData(13u, true)]  // OlthoiAcid
    public void IsOlthoi_TrueOnlyForTheTwoOlthoiVariants(uint heritageId, bool expected)
    {
        ChargenHeritageOptions heritage = MakeHeritage(heritageId, "Test");

        Assert.Equal(expected, heritage.IsOlthoi);
    }
}
