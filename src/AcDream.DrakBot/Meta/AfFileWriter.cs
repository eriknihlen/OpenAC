using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Saves MetaRules to .af (metaf) format.
/// </summary>
public static class AfFileWriter
{
    // .af keyword ↔ type now lives in MetaSchema (§3.3 single source of truth).

    // ── Public API ──────────────────────────────────────────────────────────

    public static string SaveToString(List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        var sb = new System.Text.StringBuilder();
        using var writer = new StringWriter(sb);
        WriteContent(writer, rules, embeddedNavs);
        return sb.ToString();
    }

    public static void Save(string filePath, List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        using var writer = new StreamWriter(filePath, false);
        WriteContent(writer, rules, embeddedNavs);
    }

    private static void WriteContent(TextWriter writer, List<MetaRule> rules,
        IReadOnlyDictionary<string, List<string>> embeddedNavs)
    {
        // Group rules by state
        var grouped = rules
            .GroupBy(r => r.State)
            .OrderBy(g => g.Key)
            .ToList();

        // Collect embedded nav routes referenced by rules
        var navNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNavNames(rules, navNames);

        // Write states
        foreach (var group in grouped)
        {
            writer.WriteLine($"STATE: {{{group.Key}}} ~~ {{");
            foreach (var rule in group)
                WriteRule(writer, rule);
            writer.WriteLine("~~ }");
        }

        // Write NAV sections for referenced nav routes (in-memory only)
        foreach (string navName in navNames)
        {
            if (embeddedNavs.TryGetValue(navName, out var content))
                WriteNavSection(writer, navName, content);
        }
    }

    // ── Rule writing ────────────────────────────────────────────────────────

    private static void WriteRule(TextWriter writer, MetaRule rule)
    {
        // Disabled marker (backward-compatible: a plain ~~ comment to old
        // parsers, recognised by AfFileParser to set Enabled=false).
        if (!rule.Enabled)
            writer.WriteLine("\t~~ @disabled");

        // Write IF: line
        writer.Write("\tIF:\t");
        WriteCondition(writer, rule, "\t\t\t\t");
        writer.WriteLine();

        // Write DO: line
        writer.Write("\t\tDO:\t");
        WriteAction(writer, rule, "\t\t\t\t\t");
        writer.WriteLine();
    }

    private static void WriteCondition(TextWriter writer, MetaRule rule, string childIndent)
    {
        string keyword = MetaSchema.ConditionKeyword(rule.Condition);

        // §2.6: typed vitals (MainHealthLE…VitaePHE) used to be written as
        // Expr{…} here and reloaded as a generic Expression (lossy + slow).
        // They now have real keywords in MetaSchema and fall through to the
        // bare-numeric writer below, so they round-trip to the typed enum.

        if (rule.Condition == MetaConditionType.All ||
            rule.Condition == MetaConditionType.Any ||
            rule.Condition == MetaConditionType.Not)
        {
            writer.Write(keyword);
            if (rule.Children != null)
            {
                foreach (var child in rule.Children)
                {
                    writer.WriteLine();
                    writer.Write(childIndent);
                    WriteCondition(writer, child, childIndent + "\t");
                }
            }
            return;
        }

        // Simple conditions
        string data = rule.ConditionData ?? "";
        switch (rule.Condition)
        {
            case MetaConditionType.ChatMessage:
            case MetaConditionType.Expression:
            case MetaConditionType.PackSlots_LE:
            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
            case MetaConditionType.BurdenPercentage_GE:
            case MetaConditionType.DistAnyRoutePT_GE:
            case MetaConditionType.NoMonstersWithinDistance:
            case MetaConditionType.Landblock_EQ:
            case MetaConditionType.Landcell_EQ:
            case MetaConditionType.MainHealthLE:
            case MetaConditionType.MainHealthPHE:
            case MetaConditionType.MainManaLE:
            case MetaConditionType.MainManaPHE:
            case MetaConditionType.MainStamLE:
            case MetaConditionType.VitaePHE:
                // Bare values (no braces): MainSlotsLE 4, MainHealthLE 50
                writer.Write($"{keyword} {data}");
                break;

            case MetaConditionType.ChatMessageCapture:
                writer.Write($"{keyword} {{{data}}} {{}}");
                break;

            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                // Format: ItemCountLE <count> {<name>}
                var parts = data.Split(',');
                string name = parts.Length > 0 ? parts[0] : "";
                string count = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {count} {{{name}}}");
                break;
            }

            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                // Format: MobsInDist_Name <count> <distance> {<name>}
                var parts = data.Split(',');
                string name = parts.Length > 0 ? parts[0] : "";
                string dist = parts.Length > 1 ? parts[1] : "0";
                string count = parts.Length > 2 ? parts[2] : "0";
                writer.Write($"{keyword} {count} {dist} {{{name}}}");
                break;
            }

            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                // Format: MobsInDist_Priority <count> <distance>
                var parts = data.Split(',');
                string count = parts.Length > 0 ? parts[0] : "0";
                string dist = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {count} {dist}");
                break;
            }

            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                // Format: SecsOnSpellGE <spell_id> <seconds>
                var parts = data.Split(',');
                string spellId = parts.Length > 0 ? parts[0] : "0";
                string secs = parts.Length > 1 ? parts[1] : "0";
                writer.Write($"{keyword} {spellId} {secs}");
                break;
            }

            default:
                // No-data conditions
                writer.Write(keyword);
                break;
        }
    }

    private static void WriteAction(TextWriter writer, MetaRule rule, string childIndent)
    {
        string keyword = MetaSchema.ActionKeyword(rule.Action);

        if (rule.Action == MetaActionType.All)
        {
            writer.Write(keyword);
            var children = rule.ActionChildren ?? rule.Children ?? new List<MetaRule>();
            foreach (var child in children)
            {
                writer.WriteLine();
                writer.Write(childIndent);
                WriteAction(writer, child, childIndent + "\t");
            }
            return;
        }

        string data = rule.ActionData ?? "";

        switch (rule.Action)
        {
            case MetaActionType.SetMetaState:
            case MetaActionType.ChatCommand:
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
            case MetaActionType.DestroyView:
                writer.Write($"{keyword} {{{data}}}");
                break;

            case MetaActionType.CallMetaState:
                // Format: CallState {<target>} {<return>}
                writer.Write($"{keyword} {{{data}}} {{{rule.State}}}");
                break;

            case MetaActionType.EmbeddedNavRoute:
            {
                string navName = string.IsNullOrEmpty(data) ? "empty" : data.Split(';')[0];
                writer.Write($"{keyword} {navName} {{[none]}}");
                break;
            }

            case MetaActionType.SetWatchdog:
            {
                // Format: SetWatchdog <distance> <seconds> {<state>}
                var parts = data.Split(';');
                string state = parts.Length > 0 ? parts[0] : "Default";
                string range = parts.Length > 1 ? parts[1] : "10";
                string time = parts.Length > 2 ? parts[2] : "60";
                writer.Write($"{keyword} {range} {time} {{{state}}}");
                break;
            }

            case MetaActionType.SetRAOption:
            {
                var parts = data.Split(';');
                string option = parts.Length > 0 ? parts[0] : "";
                string value = parts.Length > 1 ? parts[1] : "";
                writer.Write($"{keyword} {{{option}}} {{{value}}}");
                break;
            }

            case MetaActionType.GetRAOption:
            {
                var parts = data.Split(';');
                string variable = parts.Length > 0 ? parts[0] : "";
                string option = parts.Length > 1 ? parts[1] : "";
                writer.Write($"{keyword} {{{variable}}} {{{option}}}");
                break;
            }

            case MetaActionType.CreateView:
            {
                int semi = data.IndexOf(';');
                string name = semi >= 0 ? data.Substring(0, semi) : data;
                string xml = semi >= 0 ? data.Substring(semi + 1) : "";
                writer.Write($"{keyword} {{{name}}} {{{xml}}}");
                break;
            }

            default:
                // No-data actions
                writer.Write(keyword);
                break;
        }
    }

    // ── NAV section writing ─────────────────────────────────────────────────

    private static void WriteNavSection(TextWriter writer, string navName, IReadOnlyList<string> lines)
    {
        if (lines.Count < 3) return;

        // Verbatim, count-prefixed passthrough of the canonical uTank2 nav lines
        // (the same representation MetFileParser/NavRouteParser use). Lossless for
        // EVERY waypoint type. The previous per-token writer assumed an 11-line
        // type-6 trailer (vs the canonical 6: name,class,tie,exitEW,exitNS,exitZ)
        // and had no case for type 5 (Vendor) / 7 (NPC) at all, so it desynced and
        // truncated every .met-sourced route at its first Portal/Vendor/NPC point.
        // Count prefix means a waypoint string that happens to contain "~~ }"
        // cannot terminate the block early. Legacy human-readable NAV: sections are
        // still parsed on read for metaf-file compatibility.
        writer.WriteLine($"NAVDATA: {navName} {lines.Count} ~~ {{");
        foreach (string l in lines) writer.WriteLine(l);
        writer.WriteLine("~~ }");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CollectNavNames(List<MetaRule> rules, HashSet<string> navNames)
    {
        foreach (var rule in rules)
        {
            if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                !string.IsNullOrEmpty(rule.ActionData))
            {
                navNames.Add(rule.ActionData.Split(';')[0]);
            }

            if (rule.ActionChildren != null)
                CollectNavNames(rule.ActionChildren, navNames);
            if (rule.Children != null)
                CollectNavNames(rule.Children, navNames);
        }
    }

    private static string SpellIdToRecallName(int spellId)
    {
        return spellId switch
        {
            2931 => "Recall Aphus Lassel",
            1635 => "Lifestone Recall",
            48   => "Primary Portal Recall",
            2647 => "Secondary Portal Recall",
            2645 => "Portal Recall",
            2023 => "Recall the Sanctuary",
            4213 => "Call of the Mhoire Forge",
            3865 => "Glenden Wood Recall",
            2041 => "Aerlinthe Recall",
            4084 => "Colosseum Recall",
            5541 => "Facility Hub Recall",
            5542 => "Gear Knight Recall",
            5543 => "Neftet Recall",
            6321 => "Rynthid Recall",
            6322 => "Viridian Rise Recall",
            _    => $"Spell {spellId}"
        };
    }
}
