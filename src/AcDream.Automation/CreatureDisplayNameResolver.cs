using AcDream.Content;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.Automation;

/// <summary>
/// Creature species names from the retail enum mapper chain, for the
/// appraisal panel and for plugins asking what a monster is.
/// </summary>
public sealed class CreatureDisplayNameResolver
{
    public const uint MapperDid = 0x2200000Eu;
    private readonly IReadOnlyDictionary<uint, string> _names;

    public CreatureDisplayNameResolver(
        IReadOnlyDictionary<uint, string> names)
    {
        _names = names ?? throw new ArgumentNullException(nameof(names));
    }

    public static CreatureDisplayNameResolver Load(IDatReaderWriter dats)
    {
        ArgumentNullException.ThrowIfNull(dats);
        var names = new Dictionary<uint, string>();
        var visited = new HashSet<uint>();
        uint did = MapperDid;
        while (did != 0u && visited.Add(did))
        {
            EnumMapper? mapper = dats.Get<EnumMapper>(did);
            if (mapper is null)
                break;
            foreach ((uint id, PStringBase<byte> text) in mapper.IdToStringMap)
                names.TryAdd(id, text.Value.Replace('_', ' '));
            did = mapper.BaseEnumMap;
        }
        return new CreatureDisplayNameResolver(names);
    }

    public string Resolve(int creatureType)
        => creatureType > 0
            && _names.TryGetValue((uint)creatureType, out string? name)
                ? name
                : string.Empty;
}
