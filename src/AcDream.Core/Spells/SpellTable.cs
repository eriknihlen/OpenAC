using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AcDream.Core.Spells;

public sealed class SpellTable
{
    private readonly Dictionary<uint, SpellMetadata> _byId;

    /// <summary>Empty table (no spells loaded). Useful for tests +
    /// pre-load defaults.</summary>
    public static SpellTable Empty { get; } = new(new Dictionary<uint, SpellMetadata>());

    private SpellTable(Dictionary<uint, SpellMetadata> byId)
    {
        _byId = byId;
    }

    public static SpellTable Create(IEnumerable<SpellMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var byId = new Dictionary<uint, SpellMetadata>();
        foreach (SpellMetadata spell in metadata)
        {
            ArgumentNullException.ThrowIfNull(spell);
            if (!byId.TryAdd(spell.SpellId, spell))
                throw new ArgumentException(
                    $"Duplicate spell id 0x{spell.SpellId:X8}.", nameof(metadata));
        }
        return new SpellTable(byId);
    }

    /// <summary>Number of spells loaded.</summary>
    public int Count => _byId.Count;

    /// <summary>Look up metadata by spell id. Returns <c>true</c> if
    /// found; <paramref name="meta"/> is the matching record.</summary>
    public bool TryGet(uint spellId, out SpellMetadata meta)
    {
        if (_byId.TryGetValue(spellId, out var v))
        {
            meta = v;
            return true;
        }
        meta = null!;
        return false;
    }

    /// <summary>All loaded spell IDs. Stable enumeration order is not
    /// guaranteed.</summary>
    public IEnumerable<uint> SpellIds => _byId.Keys;

    public static SpellTable LoadFromCsv(string csvPath)
    {
        if (!File.Exists(csvPath))
            throw new FileNotFoundException("spells.csv not found", csvPath);
        using var reader = new StreamReader(csvPath);
        return LoadFromReader(reader);
    }

    public static SpellTable LoadFromReader(TextReader reader)
    {
        var byId = new Dictionary<uint, SpellMetadata>();

        string? header = reader.ReadLine();
        if (header is null) return new SpellTable(byId);

        var columns = ParseRow(header);
        var colIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < columns.Count; i++)
            colIndex[columns[i]] = i;

        int? Get(string name) => colIndex.TryGetValue(name, out int i) ? i : (int?)null;

        int? iSpellId     = Get("Spell ID");
        int? iName        = Get("Name");
        int? iSchool      = Get("School");
        int? iFamily      = Get("Family");
        int? iIconHex     = Get("IconId [Hex]");
        int? iWords       = Get("Spell Words");
        int? iDuration    = Get("Duration");
        int? iMana        = Get("Mana");
        int? iIsDebuff    = Get("IsDebuff");
        int? iIsFellow    = Get("IsFellowship");
        int? iDescription = Get("Description");
        int? iSortKey     = Get("SortKey");
        int? iDifficulty  = Get("Difficulty");
        int? iFlags       = Get("Flags [Hex]");
        int? iGeneration  = Get("Generation");
        int? iFastWindup  = Get("IsFastWindup");
        int? iOffensive   = Get("IsOffensive");
        int? iUntargeted  = Get("IsUntargetted");
        int? iSpeed       = Get("Speed");
        int? iCasterFx    = Get("CasterEffect");
        int? iTargetFx    = Get("TargetEffect");
        int? iTargetMask  = Get("TargetMask [Hex]");
        int? iSpellType   = Get("Type");

        if (iSpellId is null || iName is null) return new SpellTable(byId);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseRow(line);
            if (fields.Count <= iSpellId.Value) continue;

            if (!uint.TryParse(fields[iSpellId.Value], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint spellId))
                continue;

            string name        = iName        is int n  && n  < fields.Count ? fields[n]  : "";
            string school      = iSchool      is int s  && s  < fields.Count ? fields[s]  : "";
            uint   family      = ParseUInt(fields, iFamily);
            uint   iconId      = ParseHexUInt(fields, iIconHex);
            string words       = iWords       is int w  && w  < fields.Count ? fields[w]  : "";
            float  duration    = ParseFloat(fields, iDuration);
            int    mana        = (int)ParseUInt(fields, iMana);
            bool   isDebuff    = ParseBool(fields, iIsDebuff);
            bool   isFellow    = ParseBool(fields, iIsFellow);
            string description = iDescription is int d  && d  < fields.Count ? fields[d]  : "";
            int sortKey         = (int)ParseUInt(fields, iSortKey);
            int difficulty      = (int)ParseUInt(fields, iDifficulty);
            uint flags          = ParseHexUInt(fields, iFlags);
            int generation      = (int)ParseUInt(fields, iGeneration);
            bool fastWindup     = ParseBool(fields, iFastWindup);
            bool offensive      = ParseBool(fields, iOffensive);
            bool untargeted     = ParseBool(fields, iUntargeted);
            float speed         = ParseFloat(fields, iSpeed);
            uint casterEffect   = ParseUInt(fields, iCasterFx);
            uint targetEffect   = ParseUInt(fields, iTargetFx);
            uint targetMask     = ParseHexUInt(fields, iTargetMask);
            int spellType       = (int)ParseUInt(fields, iSpellType);

            byId[spellId] = new SpellMetadata(
                spellId, name, school, family, iconId, words, duration,
                mana, isDebuff, isFellow, description, sortKey, difficulty,
                flags, generation, fastWindup, offensive, untargeted, speed,
                casterEffect, targetEffect, targetMask, spellType)
            {
                SchoolId = ParseSchool(school),
            };
        }

        return new SpellTable(byId);
    }

    private static uint ParseUInt(IList<string> fields, int? index)
    {
        if (index is not int i || i >= fields.Count) return 0;
        return uint.TryParse(fields[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out uint v) ? v : 0u;
    }

    private static uint ParseHexUInt(IList<string> fields, int? index)
    {
        if (index is not int i || i >= fields.Count) return 0;
        string s = fields[i];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : 0u;
    }

    private static float ParseFloat(IList<string> fields, int? index)
    {
        if (index is not int i || i >= fields.Count) return 0f;
        return float.TryParse(fields[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
    }

    private static bool ParseBool(IList<string> fields, int? index)
    {
        if (index is not int i || i >= fields.Count) return false;
        return string.Equals(fields[i], "True", StringComparison.OrdinalIgnoreCase);
    }

    private static MagicSchool ParseSchool(string school) => school switch
    {
        "War Magic" => MagicSchool.WarMagic,
        "Life Magic" => MagicSchool.LifeMagic,
        "Item Enchantment" => MagicSchool.ItemEnchantment,
        "Creature Enchantment" => MagicSchool.CreatureEnchantment,
        "Void Magic" => MagicSchool.VoidMagic,
        _ => MagicSchool.None,
    };

    public static List<string> ParseRow(string row)
    {
        var fields = new List<string>();
        int i = 0;
        while (i < row.Length)
        {
            string field;
            if (row[i] == '"')
            {
                i++; // skip opening
                var sb = new System.Text.StringBuilder();
                while (i < row.Length)
                {
                    if (row[i] == '"')
                    {
                        if (i + 1 < row.Length && row[i + 1] == '"')
                        {
                            sb.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    sb.Append(row[i]);
                    i++;
                }
                field = sb.ToString();
            }
            else
            {
                int start = i;
                while (i < row.Length && row[i] != ',')
                    i++;
                field = row[start..i];
            }
            fields.Add(field);
            if (i < row.Length && row[i] == ',')
                i++;
        }
        return fields;
    }
}
