using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AcDream.Core.Spells;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Enums;
using DatReaderWriter.Types;
using CoreMagicSchool = AcDream.Core.Spells.MagicSchool;

namespace AcDream.Content;

public static class RetailSpellMetadataProjector
{
    public static SpellMetadata Project(
        uint spellId,
        SpellBase spell,
        SpellComponentTable? componentTable)
    {
        ArgumentNullException.ThrowIfNull(spell);

        uint[] formula = spell.Components.Take(8).ToArray();
        uint flags = (uint)spell.Bitfield;
        uint formulaTargetType = RetailSpellFormula.GetTargetingType(formula);
        uint targetMask = formulaTargetType;
        int level = RetailSpellFormula.InqSpellLevelByRoughHeuristic(formula);
        bool selfTargeted = (flags & (uint)SpellFlags.SelfTargeted) != 0u;
        bool beneficial = (flags & (uint)SpellFlags.Beneficial) != 0u;

        return new SpellMetadata(
            spellId,
            spell.Name.Value,
            SchoolName(spell.School),
            (uint)spell.Category,
            spell.Icon,
            BuildSpellWords(formula, componentTable),
            checked((float)spell.Duration),
            checked((int)spell.BaseMana),
            (flags & (uint)SpellFlags.Reversed) != 0u,
            (flags & (uint)SpellFlags.FellowshipSpell) != 0u,
            spell.Description.Value,
            unchecked((int)spell.DisplayOrder),
            checked((int)spell.Power),
            flags,
            level,
            (flags & (uint)SpellFlags.FastCast) != 0u,
            !beneficial && !selfTargeted,
            formulaTargetType == 0u,
            Speed: 0f,
            (uint)spell.CasterEffect,
            (uint)spell.TargetEffect,
            targetMask,
            checked((int)spell.MetaSpellType))
        {
            SchoolId = ToCoreSchool(spell.School),
            FormulaComponents = formula,
            FormulaVersion = spell.FormulaVersion,
            ComponentLoss = spell.ComponentLoss,
            BaseRangeConstant = spell.BaseRangeConstant,
            BaseRangeModifier = spell.BaseRangeMod,
            SpellEconomyModifier = spell.SpellEconomyMod,
            FizzleEffect = (uint)spell.FizzleEffect,
            RecoveryInterval = spell.RecoveryInterval,
            RecoveryAmount = spell.RecoveryAmount,
            NonComponentTargetType = (uint)spell.NonComponentTargetType,
            FormulaTargetType = formulaTargetType,
            ManaModifier = spell.ManaMod,
            DegradeModifier = spell.DegradeModifier,
            DegradeLimit = spell.DegradeLimit,
            PortalLifetime = spell.PortalLifetime,
        };
    }

    private static string SchoolName(DatReaderWriter.Enums.MagicSchool school) => school switch
    {
        DatReaderWriter.Enums.MagicSchool.WarMagic => "War Magic",
        DatReaderWriter.Enums.MagicSchool.LifeMagic => "Life Magic",
        DatReaderWriter.Enums.MagicSchool.ItemEnchantment => "Item Enchantment",
        DatReaderWriter.Enums.MagicSchool.CreatureEnchantment => "Creature Enchantment",
        DatReaderWriter.Enums.MagicSchool.VoidMagic => "Void Magic",
        _ => "None",
    };

    private static CoreMagicSchool ToCoreSchool(DatReaderWriter.Enums.MagicSchool school) => school switch
    {
        DatReaderWriter.Enums.MagicSchool.WarMagic => CoreMagicSchool.WarMagic,
        DatReaderWriter.Enums.MagicSchool.LifeMagic => CoreMagicSchool.LifeMagic,
        DatReaderWriter.Enums.MagicSchool.ItemEnchantment => CoreMagicSchool.ItemEnchantment,
        DatReaderWriter.Enums.MagicSchool.CreatureEnchantment => CoreMagicSchool.CreatureEnchantment,
        DatReaderWriter.Enums.MagicSchool.VoidMagic => CoreMagicSchool.VoidMagic,
        _ => CoreMagicSchool.None,
    };

    private static string BuildSpellWords(
        IReadOnlyList<uint> formula,
        SpellComponentTable? componentTable)
    {
        if (componentTable is null) return string.Empty;

        string first = string.Empty;
        string second = string.Empty;
        string third = string.Empty;
        foreach (uint componentId in formula)
        {
            if (!componentTable.Components.TryGetValue(componentId, out SpellComponentBase? component))
                continue;

            switch (component.Type)
            {
                case ComponentType.Herb:
                    first = component.Text.Value;
                    break;
                case ComponentType.Powder:
                    second = component.Text.Value;
                    break;
                case ComponentType.Potion:
                    third = component.Text.Value;
                    break;
            }
        }

        string tail = second + third.ToLower(CultureInfo.InvariantCulture);
        if (tail.Length != 0)
            tail = char.ToUpperInvariant(tail[0]) + tail[1..];
        return $"{first} {tail}".Trim();
    }
}
