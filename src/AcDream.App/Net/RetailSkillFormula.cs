using AcDream.Core.CharGen;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.Net;

internal static class RetailSkillFormula
{
    public static bool TryCalculate(
        SkillFormula formula,
        uint attribute1,
        uint attribute2,
        out uint result)
    {
        ArgumentNullException.ThrowIfNull(formula);

        return TryCalculate(
            formula.AdditiveBonus,
            formula.Attribute1Multiplier,
            formula.Attribute2Multiplier,
            formula.Divisor,
            attribute1,
            attribute2,
            out result);
    }

    private static bool TryCalculate(
        int additiveBonus,
        int attribute1Multiplier,
        int attribute2Multiplier,
        int divisorStorage,
        uint attribute1,
        uint attribute2,
        out uint result)
    {
        uint divisor = unchecked((uint)divisorStorage);
        if (divisor == 0u)
        {
            result = 0u;
            return false;
        }

        uint x = unchecked((uint)attribute1Multiplier);
        uint y = unchecked((uint)attribute2Multiplier);
        uint w = unchecked((uint)additiveBonus);
        uint numerator = unchecked(x * attribute1 + y * attribute2 + w);
        result = (uint)Math.Floor((double)numerator / divisor + 0.5d);
        return true;
    }

    public static uint CalculateChargenScore(
        SkillBase skillBase,
        uint attribute1,
        uint attribute2,
        ChargenSkillAdvancementClass level)
    {
        ArgumentNullException.ThrowIfNull(skillBase);

        if (!TryCalculate(skillBase.Formula, attribute1, attribute2, out uint result))
            return 0u;

        return level switch
        {
            ChargenSkillAdvancementClass.Trained => result + 5u,
            ChargenSkillAdvancementClass.Specialized => result + 10u,
            _ => result,
        };
    }

    public static uint CalculateChargenScore(
        ChargenSkillDetail skillDetail,
        uint attribute1,
        uint attribute2,
        ChargenSkillAdvancementClass level)
    {
        ChargenSkillFormula formula = skillDetail.Formula;
        if (!TryCalculate(
                formula.AdditiveBonus,
                formula.Attribute1Multiplier,
                formula.Attribute2Multiplier,
                formula.Divisor,
                attribute1,
                attribute2,
                out uint result))
        {
            return 0u;
        }

        return level switch
        {
            ChargenSkillAdvancementClass.Trained => result + 5u,
            ChargenSkillAdvancementClass.Specialized => result + 10u,
            _ => result,
        };
    }

    public static string AttributeName(DatReaderWriter.Enums.AttributeId attribute) => attribute switch
    {
        DatReaderWriter.Enums.AttributeId.Strength => "Strength",
        DatReaderWriter.Enums.AttributeId.Endurance => "Endurance",
        DatReaderWriter.Enums.AttributeId.Quickness => "Quickness",
        DatReaderWriter.Enums.AttributeId.Coordination => "Coordination",
        DatReaderWriter.Enums.AttributeId.Focus => "Focus",
        DatReaderWriter.Enums.AttributeId.Self => "Self",
        _ => string.Empty,
    };

    public static string? FormatFormula(SkillFormula formula)
    {
        ArgumentNullException.ThrowIfNull(formula);

        uint x = unchecked((uint)formula.Attribute1Multiplier);
        uint y = unchecked((uint)formula.Attribute2Multiplier);
        uint w = unchecked((uint)formula.AdditiveBonus);
        uint divisor = unchecked((uint)formula.Divisor);

        bool hasAttr1 = x >= 1 && formula.Attribute1 != 0;
        bool hasAttr2 = y >= 1 && formula.Attribute2 != 0;
        if (!hasAttr1 && !hasAttr2)
            return null;

        var text = new System.Text.StringBuilder("( ");
        if (hasAttr1 && hasAttr2)
            text.Append('(');

        if (hasAttr1)
        {
            string name1 = AttributeName(formula.Attribute1);
            text.Append(x <= 1
                ? name1
                : $"({x} x {name1})");
            if (hasAttr2)
                text.Append(" + ");
        }

        if (hasAttr2)
        {
            string name2 = AttributeName(formula.Attribute2);
            text.Append(y <= 1
                ? name2
                : $"({y} x {name2})");
        }

        if (hasAttr1 && hasAttr2)
            text.Append(')');
        if (divisor != 1)
            text.Append($" / {divisor}");
        if (w != 0)
            text.Append($"+{w}");
        text.Append(" )");
        return text.ToString();
    }

    public static string? BuildTooltip(SkillBase skillBase)
    {
        ArgumentNullException.ThrowIfNull(skillBase);

        string? formula = FormatFormula(skillBase.Formula);
        string description = skillBase.Description.Value ?? string.Empty;
        string tooltip = (formula is null ? string.Empty : formula + "\n") + description;
        return tooltip.Length == 0 ? null : tooltip;
    }
}

internal sealed class LiveSkillCreditResolver(SkillTable? skillTable)
{
    public uint Resolve(
        uint skillId,
        IReadOnlyDictionary<uint, uint> attributeCurrents)
    {
        ArgumentNullException.ThrowIfNull(attributeCurrents);

        if (skillTable?.Skills is null
            || !skillTable.Skills.TryGetValue(
                (DatReaderWriter.Enums.SkillId)skillId,
                out var skillBase))
        {
            return 0u;
        }

        SkillFormula formula = skillBase.Formula;
        attributeCurrents.TryGetValue(
            (uint)formula.Attribute1,
            out uint attribute1);
        attributeCurrents.TryGetValue(
            (uint)formula.Attribute2,
            out uint attribute2);
        return RetailSkillFormula.TryCalculate(
            formula,
            attribute1,
            attribute2,
            out uint result)
            ? result
            : 0u;
    }
}

internal sealed class ChargenSkillScoreResolver(ChargenOptions options)
{
    public uint Resolve(
        uint skillId,
        ChargenAttributeValues attributes,
        ChargenSkillAdvancementClass level)
    {
        if (!options.TryGetSkillDetail(skillId, out ChargenSkillDetail skillDetail))
        {
            return 0u;
        }

        uint attribute1 = ResolveAttribute(skillDetail.Formula.Attribute1, attributes);
        uint attribute2 = ResolveAttribute(skillDetail.Formula.Attribute2, attributes);
        return RetailSkillFormula.CalculateChargenScore(skillDetail, attribute1, attribute2, level);
    }

    private static uint ResolveAttribute(
        uint attributeId,
        ChargenAttributeValues attributes) => attributeId switch
    {
        1u => (uint)Math.Max(0, attributes.Strength),
        2u => (uint)Math.Max(0, attributes.Endurance),
        3u => (uint)Math.Max(0, attributes.Quickness),
        4u => (uint)Math.Max(0, attributes.Coordination),
        5u => (uint)Math.Max(0, attributes.Focus),
        6u => (uint)Math.Max(0, attributes.Self),
        _ => 0u,
    };
}
