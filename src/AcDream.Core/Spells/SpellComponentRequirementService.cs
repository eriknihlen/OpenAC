using System;
using System.Collections.Generic;
using System.Linq;
using AcDream.Core.Items;

namespace AcDream.Core.Spells;

public sealed record SpellFormulaDefinition(
    uint FormulaVersion,
    IReadOnlyList<uint> ComponentIds,
    uint School = 0u);

public sealed class SpellComponentRequirementService
{
    public const uint SpellComponentsRequiredProperty = 68u;

    private readonly ClientObjectTable _objects;
    private readonly Func<uint> _playerGuid;
    private readonly Func<string> _accountName;
    private readonly IReadOnlyDictionary<uint, SpellFormulaDefinition> _formulas;
    private readonly IReadOnlyDictionary<uint, uint> _wcidByScid;
    private readonly IReadOnlyDictionary<uint, uint> _magicPackWcidBySchool;

    public SpellComponentRequirementService(
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<string> accountName,
        IReadOnlyDictionary<uint, SpellFormulaDefinition> formulas,
        IReadOnlyDictionary<uint, uint> wcidByScid,
        IReadOnlyDictionary<uint, uint>? magicPackWcidBySchool = null)
    {
        _objects = objects ?? throw new ArgumentNullException(nameof(objects));
        _playerGuid = playerGuid ?? throw new ArgumentNullException(nameof(playerGuid));
        _accountName = accountName ?? throw new ArgumentNullException(nameof(accountName));
        _formulas = formulas ?? throw new ArgumentNullException(nameof(formulas));
        _wcidByScid = wcidByScid ?? throw new ArgumentNullException(nameof(wcidByScid));
        _magicPackWcidBySchool = magicPackWcidBySchool
            ?? new Dictionary<uint, uint>();
    }

    public bool HasRequiredComponents(uint spellId)
    {
        uint playerGuid = _playerGuid();
        ClientObject? player = _objects.Get(playerGuid);
        if (player is not null
            && !player.Properties.GetBool(SpellComponentsRequiredProperty, true))
            return true;
        if (!_formulas.TryGetValue(spellId, out SpellFormulaDefinition? formula))
            return false;

        HashSet<uint> ownedWcids = _objects.Objects
            .Where(item => IsOwnedByPlayer(item, playerGuid))
            .Select(item => item.WeenieClassId)
            .ToHashSet();
        IReadOnlyList<uint> components = GetAppropriateFormula(
            formula, player, playerGuid);
        foreach (uint scid in components)
        {
            if (scid == 0u) continue;
            if (!_wcidByScid.TryGetValue(scid, out uint wcid)
                || !ownedWcids.Contains(wcid))
                return false;
        }
        return true;
    }

    public IReadOnlyList<uint> GetAppropriateFormula(uint spellId)
    {
        if (!_formulas.TryGetValue(spellId, out SpellFormulaDefinition? formula))
            return [];
        uint playerGuid = _playerGuid();
        return GetAppropriateFormula(
            formula,
            _objects.Get(playerGuid),
            playerGuid);
    }

    public bool IsComponentOwned(uint spellComponentId)
    {
        if (!_wcidByScid.TryGetValue(spellComponentId, out uint weenieClassId))
            return false;
        uint playerGuid = _playerGuid();
        return _objects.Objects.Any(item =>
            item.WeenieClassId == weenieClassId
            && IsOwnedByPlayer(item, playerGuid));
    }

    private IReadOnlyList<uint> GetAppropriateFormula(
        SpellFormulaDefinition formula,
        ClientObject? player,
        uint playerGuid)
    {
        bool componentsRequired =
            player?.Properties.GetBool(SpellComponentsRequiredProperty, true)
            ?? true;
        bool infused = SchoolInfusionProperty(formula.School) is uint propertyId
            && player?.Properties.GetInt(propertyId) > 0;
        bool carriesMagicPack = _magicPackWcidBySchool.TryGetValue(
                formula.School, out uint packWcid)
            && _objects.Objects.Any(item =>
                item.ContainerId == playerGuid
                && item.WeenieClassId == packWcid);
        return !componentsRequired || infused || carriesMagicPack
            ? RetailSpellFormula.InqScarabOnlyFormula(formula.ComponentIds)
            : RetailSpellFormula.CustomizeForAccount(
                formula.ComponentIds, formula.FormulaVersion, _accountName());
    }

    private static uint? SchoolInfusionProperty(uint school) => school switch
    {
        1u => 0x129u, // AugmentationInfusedWarMagic
        2u => 0x128u, // AugmentationInfusedLifeMagic
        3u => 0x127u, // AugmentationInfusedItemMagic
        4u => 0x126u, // AugmentationInfusedCreatureMagic
        5u => 0x148u, // AugmentationInfusedVoidMagic
        _ => null,
    };

    private bool IsOwnedByPlayer(ClientObject item, uint playerGuid)
    {
        if (playerGuid == 0u) return false;
        ClientObject current = item;
        for (int depth = 0; depth < 16; depth++)
        {
            if (current.WielderId == playerGuid || current.ContainerId == playerGuid)
                return true;
            if (current.ContainerId == 0u
                || _objects.Get(current.ContainerId) is not { } parent)
                return false;
            current = parent;
        }
        return false;
    }
}
