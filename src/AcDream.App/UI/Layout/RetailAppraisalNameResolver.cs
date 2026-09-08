using AcDream.Content;
using AcDream.Core.Items;
using DatReaderWriter;
using DatReaderWriter.DBObjs;
using DatReaderWriter.Types;

namespace AcDream.App.UI.Layout;

public sealed class RetailAppraisalNameResolver
{
    private const uint MaterialClientEnum = 0x10000001u;
    private const uint MaterialSubEnum = 1u;

    public static RetailAppraisalNameResolver Empty { get; } = new(
        new Dictionary<uint, string>(),
        new CreatureDisplayNameResolver(new Dictionary<uint, string>()));

    private readonly IReadOnlyDictionary<uint, string> _materials;
    private readonly CreatureDisplayNameResolver _creatures;

    public RetailAppraisalNameResolver(
        IReadOnlyDictionary<uint, string> materials,
        CreatureDisplayNameResolver creatures)
    {
        _materials = materials
            ?? throw new ArgumentNullException(nameof(materials));
        _creatures = creatures
            ?? throw new ArgumentNullException(nameof(creatures));
    }

    public static RetailAppraisalNameResolver Load(
        IDatReaderWriter dats,
        CreatureDisplayNameResolver creatures)
    {
        ArgumentNullException.ThrowIfNull(dats);
        ArgumentNullException.ThrowIfNull(creatures);

        var materials = new Dictionary<uint, string>();
        uint masterDid = (uint)dats.Portal.Db.Header.MasterMapId;
        EnumIDMap? master = masterDid == 0u
            ? null
            : dats.Get<EnumIDMap>(masterDid);
        if (master is not null
            && master.ClientEnumToID.TryGetValue(
                MaterialClientEnum,
                out uint materialRootMapDid)
            && dats.Get<EnumIDMap>(materialRootMapDid) is { } materialRootMap
            && materialRootMap.ClientEnumToID.TryGetValue(
                MaterialSubEnum,
                out uint materialMapDid)
            && dats.Get<DualEnumIDMap>(materialMapDid) is { } materialMap)
        {
            foreach ((uint id, PStringBase<byte> text)
                     in materialMap.ClientEnumToName)
            {
                materials.TryAdd(id, Normalize(text.Value));
            }
        }

        return new RetailAppraisalNameResolver(materials, creatures);
    }

    public string ResolveCreature(int creatureType)
        => _creatures.Resolve(creatureType);

    public string ResolveHeritage(int heritageGroup)
        => CharacterIdentityText.HeritageGroupDisplayName(heritageGroup) ?? string.Empty;

    public string ResolveMaterial(int materialType)
        => materialType > 0
           && _materials.TryGetValue((uint)materialType, out string? name)
            ? name
            : string.Empty;

    public string ResolveAppropriateName(ClientObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        string baseName = obj.GetAppropriateName();
        if (obj.MaterialType is not { } materialType)
            return baseName;

        string material = ResolveMaterial(unchecked((int)materialType));
        if (string.IsNullOrEmpty(material))
            return baseName;

        string remainder = baseName
            .Replace(material, string.Empty, StringComparison.Ordinal)
            .Trim();
        return string.IsNullOrEmpty(remainder)
            ? material
            : $"{material} {remainder}";
    }

    private static string Normalize(string value)
        => value.Replace('_', ' ');
}
