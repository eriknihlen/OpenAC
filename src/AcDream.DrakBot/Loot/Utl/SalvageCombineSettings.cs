using System.Collections.Generic;
using System.Globalization;

namespace AcDream.DrakBot.Loot.Utl;

/// <summary>
/// Per-material salvage combine bands, modeled after LootSnob/VTank's
/// SalvageCombine block. Bands describe which workmanship values may merge —
/// salvage bags only combine when they share both MaterialType and the same
/// band. Stored as raw strings (e.g. "1-6, 7-8, 9, 10") so the JSON file is
/// human-editable.
/// </summary>
public sealed class SalvageCombineSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Default bands applied to materials with no per-material override.</summary>
    public string DefaultBands { get; set; } = "1-6, 7-8, 9, 10";

    /// <summary>Per-material override (key = MaterialType id, value = bands string).</summary>
    public Dictionary<int, string> PerMaterial { get; set; } = new();

    /// <summary>
    /// First numeric line of VTank's SalvageCombine block — appears to be a
    /// version or checksum value the client writes but the parser ignores.
    /// Preserved verbatim so round-tripping a .utl file produces identical
    /// bytes. New configs default to "0".
    /// </summary>
    public string RawVersion { get; set; } = "0";

    /// <summary>
    /// Returns the band key (e.g. "1-6") that contains the given workmanship
    /// for the given material, or null if the workmanship falls outside every
    /// configured band — which the caller should treat as do-not-combine.
    /// </summary>
    public string? GetBandKey(int materialId, int workmanship)
    {
        string raw = PerMaterial.TryGetValue(materialId, out string? perMat) ? perMat : DefaultBands;
        foreach (var (min, max) in ParseBands(raw))
        {
            if (workmanship >= min && workmanship <= max)
                return $"{min}-{max}";
        }
        return null;
    }

    /// <summary>Parses "1-6, 7-8, 9, 10" → [(1,6), (7,8), (9,9), (10,10)].</summary>
    public static List<(int Min, int Max)> ParseBands(string? raw)
    {
        var result = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (string part in raw.Split(','))
        {
            string p = part.Trim();
            if (p.Length == 0) continue;
            int dash = p.IndexOf('-');
            if (dash > 0 && dash < p.Length - 1)
            {
                if (int.TryParse(p.Substring(0, dash).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int lo)
                 && int.TryParse(p.Substring(dash + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int hi))
                    result.Add((lo, hi));
            }
            else if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
            {
                result.Add((v, v));
            }
        }
        return result;
    }
}
