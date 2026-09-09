using System.Collections.Frozen;
using AcDream.Core.Items;
using AcDream.Core.Spells;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using CoreSpellTable = AcDream.Core.Spells.SpellTable;

namespace AcDream.Content;

public sealed record SpellComponentDescriptor(
    uint WeenieClassId,
    string Name,
    uint Category,
    uint IconId)
{
    public uint SpellComponentId { get; init; }
    public double BurnRate { get; init; }
    public uint GestureId { get; init; }
    public double GestureSpeed { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Word { get; init; } = string.Empty;
}

public sealed class MagicCatalog
{
    private const uint SpellTableDid = 0x0E00000Eu;
    private const uint SpellComponentTableDid = 0x0E00000Fu;
    private const uint ComponentIdMapDid = 0x27000002u;

    private readonly FrozenDictionary<uint, SpellFormulaDefinition>
        _formulaDefinitions;
    private readonly FrozenDictionary<uint, uint> _wcidByScid;
    private readonly FrozenDictionary<uint, uint> _magicPackWcidBySchool;
    private readonly FrozenDictionary<uint, int> _spellLevels;

    private MagicCatalog(
        CoreSpellTable spellTable,
        FrozenDictionary<uint, SpellComponentDescriptor> components,
        FrozenDictionary<uint, SpellFormulaDefinition> formulaDefinitions,
        FrozenDictionary<uint, uint> wcidByScid,
        FrozenDictionary<uint, uint> magicPackWcidBySchool,
        FrozenDictionary<uint, int> spellLevels)
    {
        SpellTable = spellTable;
        Components = components;
        _formulaDefinitions = formulaDefinitions;
        _wcidByScid = wcidByScid;
        _magicPackWcidBySchool = magicPackWcidBySchool;
        _spellLevels = spellLevels;
    }

    public static MagicCatalog Empty { get; } = new(
        CoreSpellTable.Empty,
        EmptyFrozen<SpellComponentDescriptor>(),
        EmptyFrozen<SpellFormulaDefinition>(),
        EmptyFrozen<uint>(),
        EmptyFrozen<uint>(),
        EmptyFrozen<int>());

    public CoreSpellTable SpellTable { get; }
    public IReadOnlyDictionary<uint, SpellComponentDescriptor> Components
    {
        get;
    }

    public bool TryGetComponentBySpellComponentId(
        uint spellComponentId,
        out SpellComponentDescriptor descriptor)
    {
        if (_wcidByScid.TryGetValue(
                spellComponentId,
                out uint weenieClassId)
            && Components.TryGetValue(
                weenieClassId,
                out SpellComponentDescriptor? found))
        {
            descriptor = found;
            return true;
        }
        descriptor = null!;
        return false;
    }

    public bool IsComponentPack(uint weenieClassId) =>
        Components.ContainsKey(weenieClassId);

    public uint MagicPackWcidForSchool(uint school) =>
        _magicPackWcidBySchool.TryGetValue(school, out uint wcid) ? wcid : 0u;

    public int GetSpellLevel(uint spellId) =>
        _spellLevels.TryGetValue(spellId, out int level)
            ? level
            : 0;

    public SpellComponentRequirementService CreateRequirementService(
        ClientObjectTable objects,
        Func<uint> playerGuid,
        Func<string> accountName) =>
        new(
            objects,
            playerGuid,
            accountName,
            _formulaDefinitions,
            _wcidByScid,
            _magicPackWcidBySchool);

    public static MagicCatalog Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);

        var components =
            new Dictionary<uint, SpellComponentDescriptor>();
        var wcidByScid = new Dictionary<uint, uint>();
        SpellComponentTable? componentTable =
            dats.Get<SpellComponentTable>(SpellComponentTableDid);
        DualEnumIDMap? componentIds =
            dats.Get<DualEnumIDMap>(ComponentIdMapDid);
        if (componentTable is not null && componentIds is not null)
        {
            foreach (var pair in componentTable.Components)
            {
                if (!componentIds.ClientEnumToID.TryGetValue(
                        pair.Key,
                        out uint wcid))
                {
                    continue;
                }

                wcidByScid[pair.Key] = wcid;
                components[wcid] = new SpellComponentDescriptor(
                    wcid,
                    pair.Value.Name.Value,
                    pair.Value.Category,
                    pair.Value.Icon.DataId)
                {
                    SpellComponentId = pair.Key,
                    BurnRate = pair.Value.CDM,
                    GestureId = pair.Value.Gesture,
                    GestureSpeed = pair.Value.Time,
                    Type = pair.Value.Type.ToString(),
                    Word = pair.Value.Text.Value,
                };
            }
        }

        var metadata = new List<SpellMetadata>();
        var formulaDefinitions =
            new Dictionary<uint, SpellFormulaDefinition>();
        var spellLevels = new Dictionary<uint, int>();
        DatReaderWriter.DBObjs.SpellTable spellTable =
            dats.Get<DatReaderWriter.DBObjs.SpellTable>(SpellTableDid)
            ?? throw new InvalidOperationException(
                $"Required retail SpellTable 0x{SpellTableDid:X8} is missing from portal.dat.");
        foreach (var pair in spellTable.Spells)
        {
            SpellMetadata spell = RetailSpellMetadataProjector.Project(
                pair.Key,
                pair.Value,
                componentTable);
            metadata.Add(spell);
            uint[] formula = spell.FormulaComponents.ToArray();
            formulaDefinitions[pair.Key] = new SpellFormulaDefinition(
                spell.FormulaVersion,
                formula,
                (uint)spell.SchoolId);
            spellLevels[pair.Key] = spell.Generation;
        }

        var magicPackWcidBySchool = new Dictionary<uint, uint>();
        uint magicPackMapDid = RetailDataIdResolver.Resolve(
            dats,
            enumValue: 0x4u,
            enumCategory: 0x10000001u);
        if (magicPackMapDid != 0u
            && dats.Portal.TryGet<EnumIDMap>(
                magicPackMapDid,
                out EnumIDMap? magicPackMap)
            && magicPackMap is not null)
        {
            foreach (var pair in magicPackMap.ClientEnumToID)
                magicPackWcidBySchool[pair.Key] = pair.Value;
        }

        return new MagicCatalog(
            CoreSpellTable.Create(metadata),
            components.ToFrozenDictionary(),
            formulaDefinitions.ToFrozenDictionary(),
            wcidByScid.ToFrozenDictionary(),
            magicPackWcidBySchool.ToFrozenDictionary(),
            spellLevels.ToFrozenDictionary());
    }

    private static FrozenDictionary<uint, TValue> EmptyFrozen<TValue>() =>
        new Dictionary<uint, TValue>().ToFrozenDictionary();
}
