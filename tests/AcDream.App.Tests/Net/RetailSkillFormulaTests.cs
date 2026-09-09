using System.Collections.Frozen;
using AcDream.App.Net;
using AcDream.Core.CharGen;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;

namespace AcDream.App.Tests.Net;

public sealed class RetailSkillFormulaTests
{
    [Fact]
    public void ZeroDivisorIsTheOnlyFormulaFailureGate()
    {
        SkillFormula invalid = Formula(w: 7, x: 1, y: 1, z: 0);
        Assert.False(RetailSkillFormula.TryCalculate(invalid, 10u, 20u, out uint invalidResult));
        Assert.Equal(0u, invalidResult);

        SkillFormula zeroX = Formula(w: 7, x: 0, y: 2, z: 3);
        Assert.True(RetailSkillFormula.TryCalculate(zeroX, 10u, 4u, out uint validResult));
        Assert.Equal(5u, validResult);
    }

    [Theory]
    [InlineData(9u, 4u, 2u)]
    [InlineData(10u, 4u, 3u)]
    [InlineData(11u, 4u, 3u)]
    [InlineData(12u, 4u, 3u)]
    public void DivisionRoundsToNearestWithExactHalvesUp(
        uint numerator,
        uint divisor,
        uint expected)
    {
        SkillFormula formula = Formula(w: 0, x: 1, y: 0, z: divisor);

        Assert.True(RetailSkillFormula.TryCalculate(
            formula,
            numerator,
            0u,
            out uint result));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void AdditiveWordIsInsideTheNumerator()
    {
        SkillFormula formula = Formula(w: 3, x: 1, y: 1, z: 4);

        Assert.True(RetailSkillFormula.TryCalculate(formula, 4u, 2u, out uint result));

        Assert.Equal(2u, result);
    }

    [Fact]
    public void NumeratorUsesUnsignedThirtyTwoBitWrap()
    {
        SkillFormula formula = Formula(w: 1, x: 2, y: 0, z: 2);

        Assert.True(RetailSkillFormula.TryCalculate(
            formula,
            uint.MaxValue,
            0u,
            out uint result));

        Assert.Equal(0x80000000u, result);
    }

    [Fact]
    public void SignedDatStorageIsReinterpretedAsRetailUnsignedWords()
    {
        SkillFormula formula = Formula(w: -1, x: 0, y: 0, z: 1);

        Assert.True(RetailSkillFormula.TryCalculate(formula, 0u, 0u, out uint result));

        Assert.Equal(uint.MaxValue, result);
    }

    [Fact]
    public void FormatFormula_MultiplierIsReinterpretedAsRetailUnsignedWord()
    {
        SkillFormula formula = Formula(w: 0, x: -1, y: 0, z: 1);
        formula.Attribute1 = AttributeId.Strength;

        string? text = RetailSkillFormula.FormatFormula(formula);

        Assert.NotNull(text);
        Assert.Contains(uint.MaxValue.ToString(), text);
        Assert.DoesNotContain("-1", text);
    }

    /// <summary>Same reinterpretation, but for the divisor and additive-bonus
    /// suffixes rather than the multiplier — both must read unsigned too.</summary>
    [Fact]
    public void FormatFormula_DivisorAndAdditiveBonusAreReinterpretedAsRetailUnsignedWords()
    {
        SkillFormula formula = Formula(w: -1, x: 1, y: 0, z: unchecked((uint)-2));
        formula.Attribute1 = AttributeId.Strength;

        string? text = RetailSkillFormula.FormatFormula(formula);

        Assert.NotNull(text);
        Assert.Contains($"/ {unchecked((uint)-2)}", text);
        Assert.Contains($"+{uint.MaxValue}", text);
        Assert.DoesNotContain("-1", text);
        Assert.DoesNotContain("-2", text);
    }

    [Fact]
    public void LiveResolverLooksUpTheDatFormulaAndTreatsMissingAttributesAsZero()
    {
        const uint skillId = 0x345u;
        const uint firstAttributeId = 1u;
        const uint secondAttributeId = 2u;
        var skillTable = new SkillTable();
        skillTable.Skills.Add((SkillId)skillId, new SkillBase
        {
            Formula = new SkillFormula
            {
                AdditiveBonus = 1,
                Attribute1Multiplier = 1,
                Attribute2Multiplier = 2,
                Divisor = 3,
                Attribute1 = (AttributeId)firstAttributeId,
                Attribute2 = (AttributeId)secondAttributeId,
            },
        });
        var resolver = new LiveSkillCreditResolver(skillTable);

        uint result = resolver.Resolve(
            skillId,
            new Dictionary<uint, uint> { [firstAttributeId] = 8u });

        Assert.Equal(3u, result);
        Assert.Equal(0u, resolver.Resolve(0x999u, new Dictionary<uint, uint>()));
    }

    [Fact]
    public void BuildTooltip_FormulaFirstThenNewlineThenDescription()
    {
        SkillFormula formula = Formula(w: 0, x: 1, y: 1, z: 2);
        formula.Attribute1 = DatReaderWriter.Enums.AttributeId.Strength;
        formula.Attribute2 = DatReaderWriter.Enums.AttributeId.Coordination;
        var skillBase = new SkillBase
        {
            Formula = formula,
            Description = { Value = "Description text." },
        };

        string? tooltip = RetailSkillFormula.BuildTooltip(skillBase);

        Assert.NotNull(tooltip);
        Assert.False(tooltip!.StartsWith('\n'));
        int split = tooltip.IndexOf('\n');
        Assert.True(split > 0);
        Assert.Equal("Description text.", tooltip[(split + 1)..]);
    }

    [Fact]
    public void BuildTooltip_FormulaLessSkillShowsBareDescription()
    {
        var skillBase = new SkillBase
        {
            Formula = Formula(w: 7, x: 1, y: 1, z: 0),
            Description = { Value = "Salvage things." },
        };

        Assert.Equal("Salvage things.", RetailSkillFormula.BuildTooltip(skillBase));
    }

    private static SkillFormula Formula(int w, int x, int y, uint z) => new()
    {
        AdditiveBonus = w,
        Attribute1Multiplier = x,
        Attribute2Multiplier = y,
        Divisor = unchecked((int)z),
    };


    [Theory]
    [InlineData(ChargenSkillAdvancementClass.Untrained, 5u)]
    [InlineData(ChargenSkillAdvancementClass.Trained, 10u)]
    [InlineData(ChargenSkillAdvancementClass.Specialized, 15u)]
    public void CalculateChargenScore_AddsTheLevelBonusOnTopOfTheBaseFormula(
        ChargenSkillAdvancementClass level,
        uint expected)
    {
        var skillBase = new SkillBase { Formula = Formula(w: 0, x: 1, y: 0, z: 1) };

        uint result = RetailSkillFormula.CalculateChargenScore(skillBase, attribute1: 5u, attribute2: 0u, level);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateChargenScore_ZeroDivisor_ReturnsZero_RegardlessOfLevel()
    {
        var skillBase = new SkillBase { Formula = Formula(w: 7, x: 1, y: 1, z: 0) };

        Assert.Equal(0u, RetailSkillFormula.CalculateChargenScore(
            skillBase, 10u, 20u, ChargenSkillAdvancementClass.Untrained));
        Assert.Equal(0u, RetailSkillFormula.CalculateChargenScore(
            skillBase, 10u, 20u, ChargenSkillAdvancementClass.Trained));
        Assert.Equal(0u, RetailSkillFormula.CalculateChargenScore(
            skillBase, 10u, 20u, ChargenSkillAdvancementClass.Specialized));
    }

    [Theory]
    [InlineData(AttributeId.Strength)]
    [InlineData(AttributeId.Endurance)]
    [InlineData(AttributeId.Coordination)]
    [InlineData(AttributeId.Quickness)]
    [InlineData(AttributeId.Focus)]
    [InlineData(AttributeId.Self)]
    public void ChargenSkillScoreResolver_ResolvesEachAttributeIdThroughTheSixWaySwitch(
        AttributeId attributeId)
    {
        const uint skillId = 0x10u;
        var options = ChargenOptions.Empty with
        {
            GlobalSkillDetailsBySkillId = new Dictionary<uint, ChargenSkillDetail>
            {
                [skillId] = new ChargenSkillDetail(
                    skillId,
                    MinLevel: 1u,
                    Description: string.Empty,
                    new ChargenSkillFormula(
                        AdditiveBonus: 0,
                        Attribute1Multiplier: 1,
                        Attribute2Multiplier: 0,
                        Divisor: 1,
                        Attribute1: (uint)attributeId,
                        Attribute2: 0u)),
            }.ToFrozenDictionary(),
        };
        var resolver = new ChargenSkillScoreResolver(options);
        ChargenAttributeValues attributes = AttributeValuesWith(attributeId, 42);

        uint result = resolver.Resolve(skillId, attributes, ChargenSkillAdvancementClass.Untrained);

        Assert.Equal(42u, result);
    }

    [Fact]
    public void ChargenSkillScoreResolver_MissingProjectedSkill_ReturnsZero()
    {
        var resolver = new ChargenSkillScoreResolver(ChargenOptions.Empty);

        uint result = resolver.Resolve(
            0x10u,
            new ChargenAttributeValues(10, 20, 30, 40, 50, 60),
            ChargenSkillAdvancementClass.Specialized);

        Assert.Equal(0u, result);
    }

    private static ChargenAttributeValues AttributeValuesWith(AttributeId attributeId, int value) =>
        attributeId switch
        {
            AttributeId.Strength => new ChargenAttributeValues(value, 0, 0, 0, 0, 0),
            AttributeId.Endurance => new ChargenAttributeValues(0, value, 0, 0, 0, 0),
            AttributeId.Coordination => new ChargenAttributeValues(0, 0, value, 0, 0, 0),
            AttributeId.Quickness => new ChargenAttributeValues(0, 0, 0, value, 0, 0),
            AttributeId.Focus => new ChargenAttributeValues(0, 0, 0, 0, value, 0),
            AttributeId.Self => new ChargenAttributeValues(0, 0, 0, 0, 0, value),
            _ => default,
        };
}
