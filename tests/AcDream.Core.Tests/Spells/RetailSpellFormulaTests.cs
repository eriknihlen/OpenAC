using AcDream.Core.Spells;

namespace AcDream.Core.Tests.Spells;

public sealed class RetailSpellFormulaTests
{
    [Theory]
    [InlineData(1u, 1)]
    [InlineData(6u, 6)]
    [InlineData(0x6Eu, 6)]
    [InlineData(0x70u, 7)]
    [InlineData(0xC0u, 7)]
    [InlineData(0xC1u, 8)]
    [InlineData(7u, 0)]
    public void RoughHeuristic_UsesFormulaPowerComponent(uint component, int expected)
        => Assert.Equal(expected,
            RetailSpellFormula.InqSpellLevelByRoughHeuristic([component]));

    [Theory]
    [InlineData(0x31u, 0x10u)]
    [InlineData(0x39u, 0x00088B8Fu)]
    [InlineData(0x3Bu, 0x10010000u)]
    [InlineData(0xBEu, 0x10u)]
    [InlineData(0x30u, 0u)]
    public void TargetComponent_UsesRetailItemTypeMask(uint component, uint expected)
        => Assert.Equal(expected,
            RetailSpellFormula.GetTargetTypeFromComponentId(component));

    [Fact]
    public void TargetingType_UsesTargetFormulaSlotsOnly()
    {
        Assert.Equal(0x10u,
            RetailSpellFormula.GetTargetingType([1u, 2u, 3u, 4u, 5u, 0x31u]));
        Assert.Equal(0u,
            RetailSpellFormula.GetTargetingType([1u, 2u, 3u, 0x31u]));
    }

    [Fact]
    public void TargetingType_MapsFinalPopulatedFormulaComponent()
    {
        Assert.Equal(0x10u,
            RetailSpellFormula.GetTargetingType([1u, 2u, 3u, 4u, 0x39u, 0x31u]));
        Assert.Equal(0x00088B8Fu,
            RetailSpellFormula.GetTargetingType([1u, 2u, 3u, 4u, 0x39u, 0u, 0x31u]));
    }

    [Fact]
    public void CustomizeForAccount_IsDeterministic_AndOnlyChangesTapers()
    {
        uint[] formula = [6u, 63u, 10u, 64u, 20u, 30u, 65u, 40u];

        IReadOnlyList<uint> first = RetailSpellFormula.CustomizeForAccount(
            formula, 3u, "testaccount");
        IReadOnlyList<uint> second = RetailSpellFormula.CustomizeForAccount(
            formula, 3u, "testaccount");

        Assert.Equal(first, second);
        Assert.Equal(formula[0], first[0]);
        Assert.Equal(formula[1], first[1]);
        Assert.Equal(formula[2], first[2]);
        Assert.InRange(first[3], 63u, 74u);
        Assert.Equal(formula[4], first[4]);
        Assert.Equal(formula[5], first[5]);
        Assert.InRange(first[6], 63u, 74u);
        Assert.Equal(formula[7], first[7]);
    }

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(2u, 2)]
    [InlineData(3u, 3)]
    [InlineData(0x6Eu, 3)]
    [InlineData(0x70u, 4)]
    [InlineData(0xC1u, 4)]
    public void ScarabOnlyFormula_KeepsPowerAndAppendsRetailTaperCount(
        uint powerComponent,
        int expectedTapers)
    {
        IReadOnlyList<uint> result = RetailSpellFormula.InqScarabOnlyFormula(
            [powerComponent, 63u, 10u, 64u]);

        Assert.Equal(powerComponent, result[0]);
        Assert.Equal(expectedTapers, result.Count(component => component == 0xBCu));
        Assert.DoesNotContain(63u, result);
        Assert.DoesNotContain(10u, result);
    }
}
