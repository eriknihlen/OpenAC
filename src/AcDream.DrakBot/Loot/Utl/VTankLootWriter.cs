using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AcDream.DrakBot.Loot.Utl;

/// <summary>
/// Serializes a VTankLootProfile back to ".utl" text in VTank's exact format.
/// Pairs with VTankLootParser — round-tripping a parsed profile produces the
/// same logical content (line endings normalised to LF).
/// </summary>
public static class VTankLootWriter
{
    public static void Save(VTankLootProfile profile, string filePath)
    {
        File.WriteAllText(filePath, Serialize(profile));
    }

    public static string Serialize(VTankLootProfile profile)
    {
        var sb = new StringBuilder();
        bool v1Plus = profile.FileVersion >= 1;

        if (v1Plus)
        {
            sb.Append("UTL\n");
            sb.Append(profile.FileVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        sb.Append(profile.Rules.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var rule in profile.Rules)
        {
            sb.Append(rule.Name).Append('\n');

            if (v1Plus)
                sb.Append(rule.CustomExpression ?? string.Empty).Append('\n');

            sb.Append(BuildInfoLine(rule)).Append('\n');

            if (rule.Action == VTankLootAction.KeepUpTo)
                sb.Append((rule.KeepCount ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\n');

            foreach (var cond in rule.Conditions)
            {
                if (v1Plus)
                    sb.Append(cond.LengthCode ?? "0").Append('\n');
                foreach (var line in cond.DataLines)
                    sb.Append(line ?? string.Empty).Append('\n');
            }
        }

        if (profile.SalvageCombine != null)
        {
            sb.Append("SalvageCombine\n");
            sb.Append(profile.SalvageCombine.RawVersion ?? "0").Append('\n'); // VTank version/checksum
            sb.Append(profile.SalvageCombine.Enabled ? "1\n" : "0\n");        // enabled flag
            sb.Append(profile.SalvageCombine.DefaultBands ?? string.Empty).Append('\n');
            sb.Append(profile.SalvageCombine.PerMaterial.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (var kv in profile.SalvageCombine.PerMaterial)
            {
                sb.Append(kv.Key.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append(kv.Value ?? string.Empty).Append('\n');
            }
            sb.Append("0\n"); // terminator
        }

        return sb.ToString();
    }

    private static string BuildInfoLine(VTankLootRule rule)
    {
        var sb = new StringBuilder();
        sb.Append(rule.Priority.ToString(CultureInfo.InvariantCulture));
        sb.Append(';');
        sb.Append(((int)rule.Action).ToString(CultureInfo.InvariantCulture));
        foreach (var cond in rule.Conditions)
        {
            sb.Append(';');
            sb.Append(cond.NodeType.ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
