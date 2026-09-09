using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace AcDream.Core.Tests.Conformance;

public sealed record RetailCellPick(uint SeedCellId, Vector3 Position, uint PickedCellId);

public static class RetailTrace
{
    private static readonly Regex Fcl = new(
        @"^\[fcl\]\s+seed=0x(?<seed>[0-9A-Fa-f]{1,8})\s+" +
        @"px=(?<px>-?\d+(\.\d+)?)\s+py=(?<py>-?\d+(\.\d+)?)\s+pz=(?<pz>-?\d+(\.\d+)?)\s+" +
        @"picked=0x(?<picked>[0-9A-Fa-f]{1,8})\s*$",
        RegexOptions.Compiled);

    public static RetailCellPick? ParseFindCellList(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;
        var m = Fcl.Match(line);
        if (!m.Success) return null;
        var ci = CultureInfo.InvariantCulture;
        return new RetailCellPick(
            SeedCellId:  Convert.ToUInt32(m.Groups["seed"].Value, 16),
            Position:    new Vector3(
                float.Parse(m.Groups["px"].Value, ci),
                float.Parse(m.Groups["py"].Value, ci),
                float.Parse(m.Groups["pz"].Value, ci)),
            PickedCellId: Convert.ToUInt32(m.Groups["picked"].Value, 16));
    }

    /// <summary>Parse a log, skipping every non-matching line (noise/banner/other BPs).</summary>
    public static IReadOnlyList<RetailCellPick> ParseAll(IEnumerable<string> lines)
    {
        var list = new List<RetailCellPick>();
        foreach (var line in lines)
        {
            var rec = ParseFindCellList(line);
            if (rec is not null) list.Add(rec);
        }
        return list;
    }
}
