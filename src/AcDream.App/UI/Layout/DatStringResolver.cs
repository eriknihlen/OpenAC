using DatReaderWriter;
using AcDream.Content;
using DatReaderWriter.DBObjs;

namespace AcDream.App.UI.Layout;

public sealed class DatStringResolver
{
    private readonly IDatReaderWriter _dats;
    private readonly Dictionary<uint, StringTable?> _tables = new();

    public DatStringResolver(IDatReaderWriter dats)
        => _dats = dats ?? throw new ArgumentNullException(nameof(dats));

    public string? Resolve(UiStringInfoValue info)
        => Resolve(info.TableId, info.StringId, info.Token);

    public string? Resolve(uint tableId, uint stringId, int token = 0)
    {
        if (tableId == 0u || stringId == 0u)
            return null;

        if (!_tables.TryGetValue(tableId, out StringTable? table))
        {
            table = _dats.Get<StringTable>(tableId);
            _tables[tableId] = table;
        }

        if (table is null
            || !table.Strings.TryGetValue(stringId, out var entry)
            || entry.Strings.Count == 0)
            return null;

        int index = token >= 0 && token < entry.Strings.Count ? token : 0;
        return RetailStringEscapes.Unescape(entry.Strings[index].Value);
    }

    public string[]? ResolveAll(uint tableId, uint stringId)
    {
        if (tableId == 0u || stringId == 0u)
            return null;
        if (!_tables.TryGetValue(tableId, out StringTable? table))
        {
            table = _dats.Get<StringTable>(tableId);
            _tables[tableId] = table;
        }
        return table is not null
            && table.Strings.TryGetValue(stringId, out var entry)
            && entry.Strings.Count != 0
                ? entry.Strings
                    .Select(value => RetailStringEscapes.Unescape(value.Value))
                    .ToArray()
                : null;
    }

    public static readonly uint PlayerVariable = ComputeHash("PLAYER");

    public string? ResolveTemplate(
        uint tableId,
        string key,
        IReadOnlyDictionary<uint, string> variables)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ResolveTemplate(tableId, ComputeHash(key), variables);
    }

    /// <summary>
    /// The same interleave for an entry addressed by its key hash, for the
    /// templates the game keeps by id rather than by name.
    /// </summary>
    public string? ResolveTemplate(
        uint tableId,
        uint keyHash,
        IReadOnlyDictionary<uint, string> variables)
    {
        ArgumentNullException.ThrowIfNull(variables);
        if (tableId == 0u)
            return null;

        if (!_tables.TryGetValue(tableId, out StringTable? table))
        {
            table = _dats.Get<StringTable>(tableId);
            _tables[tableId] = table;
        }

        if (table is null
            || !table.Strings.TryGetValue(keyHash, out var entry)
            || entry.Strings.Count == 0)
            return null;

        var composed = new System.Text.StringBuilder();
        for (int i = 0; i < entry.Strings.Count; i++)
        {
            composed.Append(entry.Strings[i].Value);
            if (i < entry.Variables.Count
                && variables.TryGetValue(entry.Variables[i], out string? value))
            {
                composed.Append(RetailStringEscapes.Escape(value));
            }
        }
        return RetailStringEscapes.Unescape(composed.ToString());
    }

    public static uint ComputeHash(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        uint result = 0u;
        foreach (char c in value)
        {
            result = unchecked((result << 4) + (byte)c);
            uint high = result & 0xF0000000u;
            if (high != 0u)
                result = ((high >> 24) ^ result) & 0x0FFFFFFFu;
        }

        return result == uint.MaxValue ? uint.MaxValue - 1u : result;
    }
}
