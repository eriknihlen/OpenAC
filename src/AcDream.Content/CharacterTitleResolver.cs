using DatReaderWriter.DBObjs;

namespace AcDream.Content;

/// <summary>
/// The display text of a character title, read from the installed data
/// files: the title id names an entry in an enum mapping, and that entry's
/// name is the key of the text in a string table.
/// </summary>
/// <remarks>
/// This lives beside the other content catalogues rather than beside anything
/// that draws, because both clients need it: a bot reporting the titles its
/// character holds and a window captioning the character panel are the same
/// question.
/// </remarks>
public sealed class CharacterTitleResolver
{
    public const uint TitleEnumMapperId = 0x22000041u;

    public const uint TitleStringTableId = 0x2300000Eu;

    private readonly IDatReaderWriter _dats;
    private readonly Dictionary<uint, string?> _resolvedCache = new();
    private EnumMapper? _titleEnumMapper;
    private StringTable? _titleStrings;
    private bool _loaded;

    public CharacterTitleResolver(IDatReaderWriter dats)
    {
        _dats = dats ?? throw new ArgumentNullException(nameof(dats));
    }

    /// <summary>
    /// The title's text, or <see langword="null"/> for title zero and for a
    /// title the data files do not name. The caller holds whatever lock
    /// guards the data files.
    /// </summary>
    public string? Resolve(uint titleId)
    {
        if (titleId == 0u)
            return null;

        if (_resolvedCache.TryGetValue(titleId, out string? cached))
            return cached;

        if (!_loaded)
        {
            _titleEnumMapper = _dats.Get<EnumMapper>(TitleEnumMapperId);
            _titleStrings = _dats.Get<StringTable>(TitleStringTableId);
            _loaded = true;
        }

        string? resolved = null;
        if (_titleEnumMapper is not null
            && _titleStrings is not null
            && _titleEnumMapper.IdToStringMap.TryGetValue(titleId, out var rawNameValue))
        {
            string rawName = rawNameValue.ToString();
            if (!string.IsNullOrEmpty(rawName)
                && _titleStrings.Strings.TryGetValue(
                    RetailStringHash.Compute(rawName), out var entry)
                && entry.Strings.Count != 0)
            {
                resolved = RetailStringEscapes.Unescape(entry.Strings[0].Value);
            }
        }

        _resolvedCache[titleId] = resolved;
        return resolved;
    }
}
