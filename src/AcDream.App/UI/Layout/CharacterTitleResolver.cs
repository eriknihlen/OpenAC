using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI.Layout;

public sealed class CharacterTitleResolver
{
    public const uint TitleEnumMapperId = 0x22000041u;

    public const uint TitleStringTableId = 0x2300000Eu;

    private readonly IDatReaderWriter _dats;
    private readonly DatStringResolver _strings;
    private readonly Dictionary<uint, string?> _resolvedCache = new();
    private EnumMapper? _titleEnumMapper;
    private bool _loadedMapper;

    public CharacterTitleResolver(IDatReaderWriter dats)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
        _strings = new DatStringResolver(dats);
    }

    public string? Resolve(uint titleId)
    {
        if (titleId == 0u)
            return null;

        if (_resolvedCache.TryGetValue(titleId, out string? cached))
            return cached;

        if (!_loadedMapper)
        {
            _dats.Portal.TryGet<EnumMapper>(TitleEnumMapperId, out _titleEnumMapper);
            _loadedMapper = true;
        }

        string? resolved = null;
        if (_titleEnumMapper is not null
            && _titleEnumMapper.IdToStringMap.TryGetValue(titleId, out var rawNameValue))
        {
            string rawName = rawNameValue.ToString();
            if (!string.IsNullOrEmpty(rawName))
                resolved = _strings.Resolve(TitleStringTableId, DatStringResolver.ComputeHash(rawName));
        }

        _resolvedCache[titleId] = resolved;
        return resolved;
    }
}
