using AcDream.DrakBot.Navigation;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Parses metaf .af files into RynthAi MetaRule lists.
/// Embedded NAV routes are returned in <see cref="LoadedMeta.EmbeddedNavs"/>
/// and stay in memory — they are never written to the NavProfiles folder.
/// </summary>
public static class AfFileParser
{
    // .af keyword ↔ MetaConditionType/MetaActionType now lives in MetaSchema
    // (§3.3 single source of truth; replaces the read/write map pair that
    // desynced and caused the §2.6 SecsOnSpell _LE→_GE loss).

    // Per-parse warning sink (ThreadStatic: parsing is synchronous, but two
    // threads could load different metas at once). Points at the active
    // LoadedMeta.Warnings for the duration of ParseLines.
    [ThreadStatic] private static List<string>? _warn;

    // ── Public API ──────────────────────────────────────────────────────────

    public static LoadedMeta Load(string filePath)
    {
        var result = new LoadedMeta();
        if (!File.Exists(filePath)) return result;

        string[] lines = File.ReadAllLines(filePath);
        ParseLines(lines, result);
        return result;
    }

    public static LoadedMeta LoadFromText(string text)
    {
        var result = new LoadedMeta();
        // Normalize line endings then split
        string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        ParseLines(lines, result);
        return result;
    }

    private static void ParseLines(string[] lines, LoadedMeta result)
    {
        _warn = result.Warnings;
        try
        {
            int idx = 0;
            while (idx < lines.Length)
            {
                string line = lines[idx];
                string trimmed = line.TrimStart();

                // Skip comments and blank lines
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("~~"))
                {
                    idx++;
                    continue;
                }

                if (trimmed.StartsWith("STATE:"))
                {
                    ParseState(lines, ref idx, result.Rules);
                }
                else if (trimmed.StartsWith("NAVDATA:"))
                {
                    ParseNavData(lines, ref idx, result.EmbeddedNavs);
                }
                else if (trimmed.StartsWith("NAV:"))
                {
                    ParseNavSection(lines, ref idx, result.EmbeddedNavs);
                }
                else
                {
                    idx++;
                }
            }

            // Drop empty routes (header only, 0 points) and clear dead EmbedNav refs
            PruneEmbeddedNavs(result.Rules, result.EmbeddedNavs);
        }
        catch (Exception ex)
        {
            // Malformed meta yields whatever parsed so far rather than throwing
            // into the caller; record why so the loader can show it.
            result.Warnings.Add($"parse aborted: {ex.GetType().Name}: {ex.Message}");
        }
        finally { _warn = null; }
    }

    // ── State parsing ───────────────────────────────────────────────────────

    private static void ParseState(string[] lines, ref int idx, List<MetaRule> rules)
    {
        string stateName = ExtractBraceContent(lines[idx], out _);
        idx++; // consume STATE: line

        bool pendingDisabled = false;   // set by a "~~ @disabled" marker line
        while (idx < lines.Length)
        {
            string trimmed = lines[idx].TrimStart();

            if (trimmed == "~~ }" || trimmed.StartsWith("STATE:") || trimmed.StartsWith("NAV:"))
            {
                if (trimmed == "~~ }") idx++;
                return;
            }

            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("~~"))
            {
                if (trimmed.Equals("~~ @disabled", StringComparison.OrdinalIgnoreCase))
                    pendingDisabled = true;
                idx++;
                continue;
            }

            // Look for IF: line
            if (trimmed.StartsWith("IF:"))
            {
                var rule = new MetaRule { State = stateName, Enabled = !pendingDisabled };
                pendingDisabled = false;
                int ifIndent = CountLeadingTabs(lines[idx]);
                ParseIfBlock(lines, ref idx, rule, ifIndent);
                rules.Add(rule);
            }
            else
            {
                idx++;
            }
        }
    }

    private static void ParseIfBlock(string[] lines, ref int idx, MetaRule rule, int ifIndent)
    {
        // Parse condition from IF: line
        string ifLine = lines[idx].TrimStart();
        string afterIf = StripPrefix(ifLine, "IF:");
        ParseConditionLine(afterIf, rule);
        idx++;

        // If compound condition, parse children until DO: or end of state
        if (rule.Condition == MetaConditionType.All ||
            rule.Condition == MetaConditionType.Any ||
            rule.Condition == MetaConditionType.Not)
        {
            rule.Children = new List<MetaRule>();
            while (idx < lines.Length)
            {
                string trimmed = lines[idx].TrimStart();
                if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("~~"))
                {
                    if (trimmed == "~~ }") break;
                    idx++;
                    continue;
                }

                int lineIndent = CountLeadingTabs(lines[idx]);

                // DO: at expected indent = end of condition children
                if (trimmed.StartsWith("DO:") && lineIndent <= ifIndent + 1)
                    break;

                // IF: at same indent = new rule (shouldn't happen in children)
                if (trimmed.StartsWith("IF:") && lineIndent <= ifIndent)
                    break;

                // STATE: or NAV: = end of state
                if (trimmed.StartsWith("STATE:") || trimmed.StartsWith("NAV:"))
                    break;

                // This line is a child condition
                var child = new MetaRule { State = rule.State };
                ParseConditionLine(trimmed, child);
                idx++;

                // If child is compound, recursively parse its children
                if (child.Condition == MetaConditionType.All ||
                    child.Condition == MetaConditionType.Any ||
                    child.Condition == MetaConditionType.Not)
                {
                    child.Children = new List<MetaRule>();
                    int childIndent = lineIndent;
                    while (idx < lines.Length)
                    {
                        string ct = lines[idx].TrimStart();
                        if (string.IsNullOrWhiteSpace(ct) || ct.StartsWith("~~"))
                        {
                            if (ct == "~~ }") break;
                            idx++;
                            continue;
                        }

                        int ci = CountLeadingTabs(lines[idx]);
                        if (ci <= childIndent) break;
                        if (ct.StartsWith("DO:") || ct.StartsWith("IF:") ||
                            ct.StartsWith("STATE:") || ct.StartsWith("NAV:"))
                            break;

                        var grandchild = new MetaRule { State = rule.State };
                        ParseConditionLine(ct, grandchild);
                        idx++;

                        // Support one more level of nesting
                        if (grandchild.Condition == MetaConditionType.All ||
                            grandchild.Condition == MetaConditionType.Any ||
                            grandchild.Condition == MetaConditionType.Not)
                        {
                            grandchild.Children = new List<MetaRule>();
                            ParseNestedConditionChildren(lines, ref idx, grandchild, ci);
                        }

                        child.Children.Add(grandchild);
                    }
                }

                rule.Children.Add(child);
            }
        }

        // Now parse DO: line
        while (idx < lines.Length)
        {
            string trimmed = lines[idx].TrimStart();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("~~"))
            {
                if (trimmed == "~~ }") return;
                idx++;
                continue;
            }

            if (trimmed.StartsWith("DO:"))
            {
                string afterDo = StripPrefix(trimmed, "DO:");
                ParseActionLine(afterDo, rule);
                int doIndent = CountLeadingTabs(lines[idx]);
                idx++;

                // If DoAll, parse children
                if (rule.Action == MetaActionType.All)
                {
                    rule.ActionChildren = new List<MetaRule>();
                    while (idx < lines.Length)
                    {
                        string ct = lines[idx].TrimStart();
                        if (string.IsNullOrWhiteSpace(ct) || ct.StartsWith("~~"))
                        {
                            if (ct == "~~ }") break;
                            idx++;
                            continue;
                        }

                        int ci = CountLeadingTabs(lines[idx]);
                        if (ci <= doIndent) break;
                        if (ct.StartsWith("IF:") || ct.StartsWith("STATE:") || ct.StartsWith("NAV:"))
                            break;

                        var child = new MetaRule { State = rule.State };
                        ParseActionLine(ct, child);
                        idx++;

                        // Nested DoAll
                        if (child.Action == MetaActionType.All)
                        {
                            child.ActionChildren = new List<MetaRule>();
                            int childDoIndent = ci;
                            while (idx < lines.Length)
                            {
                                string ct2 = lines[idx].TrimStart();
                                if (string.IsNullOrWhiteSpace(ct2) || ct2.StartsWith("~~"))
                                {
                                    if (ct2 == "~~ }") break;
                                    idx++;
                                    continue;
                                }
                                int ci2 = CountLeadingTabs(lines[idx]);
                                if (ci2 <= childDoIndent) break;
                                if (ct2.StartsWith("IF:") || ct2.StartsWith("STATE:") || ct2.StartsWith("NAV:"))
                                    break;
                                var grandchild = new MetaRule { State = rule.State };
                                ParseActionLine(ct2, grandchild);
                                idx++;
                                child.ActionChildren.Add(grandchild);
                            }
                        }

                        rule.ActionChildren.Add(child);
                    }
                }

                return;
            }

            // Not a DO: line — shouldn't happen, skip
            if (trimmed.StartsWith("IF:") || trimmed.StartsWith("STATE:") || trimmed.StartsWith("NAV:"))
                return;
            idx++;
        }
    }

    private static void ParseNestedConditionChildren(string[] lines, ref int idx, MetaRule parent, int parentIndent)
    {
        while (idx < lines.Length)
        {
            string ct = lines[idx].TrimStart();
            if (string.IsNullOrWhiteSpace(ct) || ct.StartsWith("~~"))
            {
                if (ct == "~~ }") break;
                idx++;
                continue;
            }

            int ci = CountLeadingTabs(lines[idx]);
            if (ci <= parentIndent) break;
            if (ct.StartsWith("DO:") || ct.StartsWith("IF:") ||
                ct.StartsWith("STATE:") || ct.StartsWith("NAV:"))
                break;

            var child = new MetaRule { State = parent.State };
            ParseConditionLine(ct, child);
            idx++;

            if (child.Condition == MetaConditionType.All ||
                child.Condition == MetaConditionType.Any ||
                child.Condition == MetaConditionType.Not)
            {
                child.Children = new List<MetaRule>();
                ParseNestedConditionChildren(lines, ref idx, child, ci);
            }

            parent.Children.Add(child);
        }
    }

    // ── Line parsers ────────────────────────────────────────────────────────

    private static void ParseConditionLine(string text, MetaRule rule)
    {
        text = text.Trim();

        // Remove trailing ~~ comments
        int commentIdx = text.IndexOf("~~", StringComparison.Ordinal);
        if (commentIdx > 0)
        {
            // Make sure we're not inside a brace group
            int braceDepth = 0;
            for (int i = 0; i < commentIdx; i++)
            {
                if (text[i] == '{') braceDepth++;
                else if (text[i] == '}') braceDepth--;
            }
            if (braceDepth == 0)
                text = text.Substring(0, commentIdx).TrimEnd();
        }

        // Extract keyword (first word before space or brace)
        int spaceIdx = text.IndexOf(' ');
        int braceIdx = text.IndexOf('{');
        int keyEnd = text.Length;
        if (spaceIdx > 0 && (braceIdx < 0 || spaceIdx < braceIdx)) keyEnd = spaceIdx;
        else if (braceIdx > 0) keyEnd = braceIdx;

        string keyword = text.Substring(0, keyEnd).Trim();
        string rest = keyEnd < text.Length ? text.Substring(keyEnd).Trim() : "";

        if (!MetaSchema.TryCondition(keyword, out MetaConditionType condType))
        {
            rule.Condition = MetaConditionType.Never;
            _warn?.Add($"unknown condition keyword '{keyword}' in state '{rule.State}' — rule will never fire");
            return;
        }

        rule.Condition = condType;

        // Parse condition data based on type
        switch (condType)
        {
            case MetaConditionType.ChatMessage:
            case MetaConditionType.Expression:
                rule.ConditionData = ExtractBraceContent(rest, out _);
                break;

            case MetaConditionType.ChatMessageCapture:
            {
                // ChatCapture {pattern} {replacement} — we only use the pattern
                string pattern = ExtractBraceContent(rest, out int endPos);
                rule.ConditionData = pattern;
                break;
            }

            case MetaConditionType.InventoryItemCount_LE:
            case MetaConditionType.InventoryItemCount_GE:
            {
                // Format: ItemCountLE <count> {<item_name>}
                SplitBareAndBrace(rest, out string countStr, out string nameStr);
                rule.ConditionData = $"{nameStr},{countStr}";
                break;
            }

            case MetaConditionType.MonsterNameCountWithinDistance:
            {
                // Format: MobsInDist_Name <count> <distance> {<name>}
                SplitBareAndBrace(rest, out string bareArgs, out string name);
                string[] bp = bareArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string count = bp.Length > 0 ? bp[0] : "0";
                string dist = bp.Length > 1 ? bp[1] : "0";
                rule.ConditionData = $"{name},{dist},{count}";
                break;
            }

            case MetaConditionType.MonsterPriorityCountWithinDistance:
            {
                // Format: MobsInDist_Priority <count> <distance>
                string[] bp = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string count = bp.Length > 0 ? bp[0] : "0";
                string dist = bp.Length > 1 ? bp[1] : "0";
                rule.ConditionData = $"{count},{dist}";
                break;
            }

            case MetaConditionType.TimeLeftOnSpell_GE:
            case MetaConditionType.TimeLeftOnSpell_LE:
            {
                // Format: SecsOnSpell{GE,LE} <spell_id> <seconds>
                string[] bp = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string spellId = bp.Length > 0 ? bp[0] : "0";
                string secs = bp.Length > 1 ? bp[1] : "0";
                rule.ConditionData = $"{spellId},{secs}";
                break;
            }

            case MetaConditionType.PackSlots_LE:
            case MetaConditionType.SecondsInState_GE:
            case MetaConditionType.SecondsInStateP_GE:
            case MetaConditionType.BurdenPercentage_GE:
            case MetaConditionType.DistAnyRoutePT_GE:
            case MetaConditionType.NoMonstersWithinDistance:
            case MetaConditionType.Landblock_EQ:
            case MetaConditionType.Landcell_EQ:
            // §2.6: typed vitals now have real .af keywords (bare numeric data,
            // same as MainSlotsLE) so they round-trip instead of being written
            // as Expr{…} and reloaded as a generic Expression.
            case MetaConditionType.MainHealthLE:
            case MetaConditionType.MainHealthPHE:
            case MetaConditionType.MainManaLE:
            case MetaConditionType.MainManaPHE:
            case MetaConditionType.MainStamLE:
            case MetaConditionType.VitaePHE:
                // These use bare numbers (no braces): e.g. MainSlotsLE 4, BlockE F6820033
                rule.ConditionData = ExtractBareOrBrace(rest);
                break;

            // No-data conditions
            default:
                break;
        }
    }

    private static void ParseActionLine(string text, MetaRule rule)
    {
        text = text.Trim();

        // Remove trailing ~~ comments (same brace-aware logic)
        int commentIdx = text.IndexOf("~~", StringComparison.Ordinal);
        if (commentIdx > 0)
        {
            int braceDepth = 0;
            for (int i = 0; i < commentIdx; i++)
            {
                if (text[i] == '{') braceDepth++;
                else if (text[i] == '}') braceDepth--;
            }
            if (braceDepth == 0)
                text = text.Substring(0, commentIdx).TrimEnd();
        }

        // Extract keyword
        int spaceIdx = text.IndexOf(' ');
        int braceIdx = text.IndexOf('{');
        int keyEnd = text.Length;
        if (spaceIdx > 0 && (braceIdx < 0 || spaceIdx < braceIdx)) keyEnd = spaceIdx;
        else if (braceIdx > 0) keyEnd = braceIdx;

        string keyword = text.Substring(0, keyEnd).Trim();
        string rest = keyEnd < text.Length ? text.Substring(keyEnd).Trim() : "";

        if (!MetaSchema.TryAction(keyword, out MetaActionType actionType))
        {
            rule.Action = MetaActionType.None;
            _warn?.Add($"unknown action keyword '{keyword}' in state '{rule.State}'");
            return;
        }

        rule.Action = actionType;

        switch (actionType)
        {
            case MetaActionType.SetMetaState:
            case MetaActionType.CallMetaState:
            case MetaActionType.ChatCommand:
            case MetaActionType.ExpressionAction:
            case MetaActionType.ChatExpression:
            case MetaActionType.DestroyView:
                rule.ActionData = ExtractBraceContent(rest, out _);
                break;

            case MetaActionType.EmbeddedNavRoute:
            {
                // EmbedNav navname {type/filename}
                int braceStart = rest.IndexOf('{');
                if (braceStart >= 0)
                {
                    string navName = rest.Substring(0, braceStart).Trim();
                    // Store nav name as ActionData — will be resolved later
                    rule.ActionData = navName;
                }
                else
                {
                    rule.ActionData = rest.Trim();
                }
                break;
            }

            case MetaActionType.SetWatchdog:
            {
                // Format: SetWatchdog <distance> <seconds> {<state>}
                SplitBareAndBrace(rest, out string bareArgs, out string state);
                string[] bp = bareArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string range = bp.Length > 0 ? bp[0] : "10";
                string time = bp.Length > 1 ? bp[1] : "60";
                rule.ActionData = $"{state};{range};{time}";
                break;
            }

            case MetaActionType.SetRAOption:
            {
                string option = ExtractBraceContent(rest, out int e1);
                string r1 = e1 < rest.Length ? rest.Substring(e1).Trim() : "";
                string value = ExtractBraceContent(r1, out _);
                rule.ActionData = $"{option};{value}";
                break;
            }

            case MetaActionType.GetRAOption:
            {
                string variable = ExtractBraceContent(rest, out int e1);
                string r1 = e1 < rest.Length ? rest.Substring(e1).Trim() : "";
                string option = ExtractBraceContent(r1, out _);
                rule.ActionData = $"{variable};{option}";
                break;
            }

            case MetaActionType.CreateView:
            {
                string name = ExtractBraceContent(rest, out int e1);
                string r1 = e1 < rest.Length ? rest.Substring(e1).Trim() : "";
                string xml = ExtractBraceContent(r1, out _);
                rule.ActionData = $"{name};{xml}";
                break;
            }

            // No data
            default:
                break;
        }
    }

    // ── NAV section parsing ─────────────────────────────────────────────────

    // Reads a `NAVDATA: <name> <lineCount> ~~ {` block (written by AfFileWriter):
    // the canonical uTank2 nav lines stored verbatim. Count-prefixed so a waypoint
    // string containing "~~ }" cannot terminate the block early. This is the exact
    // representation MetFileParser produces and the nav engine consumes — so a route
    // round-trips losslessly regardless of waypoint type (Portal/Vendor/NPC included).
    private static void ParseNavData(string[] lines, ref int idx,
        Dictionary<string, List<string>> navRoutes)
    {
        string body = StripPrefix(lines[idx].TrimStart(), "NAVDATA:");
        int comment = body.IndexOf("~~", StringComparison.Ordinal);
        if (comment >= 0) body = body.Substring(0, comment).TrimEnd();
        // Trailing token is the line count; everything before it is the route name
        // (names are space-sanitized on write; parse from the right to be safe).
        int sp = body.LastIndexOf(' ');
        string name = sp > 0 ? body.Substring(0, sp).Trim() : body.Trim();
        int count = 0;
        if (sp > 0)
            int.TryParse(body.Substring(sp + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count);
        idx++; // consume header

        var navLines = new List<string>(count);
        for (int k = 0; k < count && idx < lines.Length; k++)
            navLines.Add(lines[idx++]);

        if (idx < lines.Length && lines[idx].Trim() == "~~ }") idx++; // consume closer

        if (!string.IsNullOrEmpty(name) && navLines.Count >= 3)
            navRoutes[name] = navLines;
    }

    private static void ParseNavSection(string[] lines, ref int idx, Dictionary<string, List<string>> navRoutes)
    {
        // NAV: navname type ~~ {
        string navLine = lines[idx].TrimStart();
        string afterNav = StripPrefix(navLine, "NAV:");

        // Remove trailing ~~ { comment
        int commentIdx = afterNav.IndexOf("~~", StringComparison.Ordinal);
        if (commentIdx > 0)
            afterNav = afterNav.Substring(0, commentIdx).TrimEnd();

        // Parse "navname type"
        string[] parts = afterNav.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string navName = parts.Length > 0 ? parts[0] : "";
        string navType = parts.Length > 1 ? parts[1] : "circular";
        idx++;

        // Read nav points until ~~ }
        var navContent = new List<string>();
        navContent.Add("uTank2 NAV 1.2");

        int routeTypeNum = navType.ToLowerInvariant() switch
        {
            "circular" => 1,
            "linear" => 2,
            "follow" => 3,
            "once" => 4,
            _ => 1
        };
        navContent.Add(routeTypeNum.ToString());

        var pointLines = new List<string>();
        while (idx < lines.Length)
        {
            string trimmed = lines[idx].TrimStart();
            if (trimmed == "~~ }" || trimmed.StartsWith("STATE:") || trimmed.StartsWith("NAV:"))
            {
                if (trimmed == "~~ }") idx++;
                break;
            }

            if (!string.IsNullOrWhiteSpace(trimmed) && !trimmed.StartsWith("~~"))
                pointLines.Add(trimmed);
            idx++;
        }

        // Convert .af nav points to .nav format
        var navFileLines = new List<string>();
        foreach (string pointLine in pointLines)
        {
            string[] tokens = SplitNavTokens(pointLine);
            if (tokens.Length == 0) continue;

            string nodeType = tokens[0];
            switch (nodeType)
            {
                case "pnt": // pnt EW NS Z  (VTank convention: X=EW, Y=NS)
                    if (tokens.Length >= 4)
                    {
                        navFileLines.Add("0"); // type 0 = Point
                        navFileLines.Add(tokens[1]); // EW
                        navFileLines.Add(tokens[2]); // NS
                        navFileLines.Add(tokens[3]); // Z
                        navFileLines.Add("0");
                    }
                    break;

                case "rcl": // rcl EW NS Z {RecallName}
                    if (tokens.Length >= 4)
                    {
                        navFileLines.Add("2"); // type 2 = Recall
                        navFileLines.Add(tokens[1]); // EW
                        navFileLines.Add(tokens[2]); // NS
                        navFileLines.Add(tokens[3]); // Z
                        navFileLines.Add("0");
                        // Recall spell ID — we'll use 0 as placeholder
                        string recallName = tokens.Length >= 5 ? tokens[4] : "";
                        navFileLines.Add(RecallNameToSpellId(recallName).ToString());
                    }
                    break;

                case "pau": // pau seconds
                    if (tokens.Length >= 2)
                    {
                        navFileLines.Add("3"); // type 3 = Pause
                        navFileLines.Add("0");
                        navFileLines.Add("0");
                        navFileLines.Add("0");
                        navFileLines.Add("0");
                        double.TryParse(tokens[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double pauseSec);
                        navFileLines.Add(((int)(pauseSec * 1000)).ToString());
                    }
                    break;

                case "cht": // cht EW NS Z {command}
                    if (tokens.Length >= 5)
                    {
                        navFileLines.Add("4"); // type 4 = Chat
                        navFileLines.Add(tokens[1]); // EW
                        navFileLines.Add(tokens[2]); // NS
                        navFileLines.Add(tokens[3]); // Z
                        navFileLines.Add("0");
                        navFileLines.Add(tokens[4]); // chat command
                    }
                    break;

                case "ptl": // ptl EW NS Z destEW destNS destZ objectClass {PortalName}
                    if (tokens.Length >= 8)
                    {
                        navFileLines.Add("6"); // type 6 = PortalNPC
                        navFileLines.Add(tokens[1]); // EW
                        navFileLines.Add(tokens[2]); // NS
                        navFileLines.Add(tokens[3]); // Z
                        navFileLines.Add("0");
                        string portalName = tokens.Length >= 9 ? tokens[8] : tokens[7];
                        int objClass = 14; // Portal class
                        int.TryParse(tokens[7], out objClass);
                        navFileLines.Add(portalName);
                        navFileLines.Add(objClass.ToString());
                        navFileLines.Add("False");
                        navFileLines.Add(tokens[4]); // dest EW
                        navFileLines.Add(tokens[5]); // dest NS
                        navFileLines.Add(tokens[6]); // dest Z
                        navFileLines.Add("0");
                        navFileLines.Add("0"); // land EW
                        navFileLines.Add("0"); // land NS
                        navFileLines.Add("0"); // land Z
                        navFileLines.Add("0");
                    }
                    break;

                case "vnd": // vnd EW NS Z vendorId {VendorName}
                    if (tokens.Length >= 5)
                    {
                        navFileLines.Add("6"); // treat vendor as PortalNPC type
                        navFileLines.Add(tokens[1]); // EW
                        navFileLines.Add(tokens[2]); // NS
                        navFileLines.Add(tokens[3]); // Z
                        navFileLines.Add("0");
                        string vndName = tokens.Length >= 6 ? tokens[5] : "Vendor";
                        navFileLines.Add(vndName);
                        navFileLines.Add("12"); // NPC class
                        navFileLines.Add("False");
                        navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0");
                        navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0");
                    }
                    break;

                case "tlk": // tlk EW NS Z objectId {NPCName}
                    if (tokens.Length >= 5)
                    {
                        navFileLines.Add("6");
                        navFileLines.Add(tokens[1]);
                        navFileLines.Add(tokens[2]);
                        navFileLines.Add(tokens[3]);
                        navFileLines.Add("0");
                        string tlkName = tokens.Length >= 6 ? tokens[5] : "NPC";
                        navFileLines.Add(tlkName);
                        navFileLines.Add("37"); // NPC class
                        navFileLines.Add("False");
                        navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0");
                        navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0"); navFileLines.Add("0");
                    }
                    break;

                case "flw": // flw objectId {Name}
                    if (tokens.Length >= 2)
                    {
                        // Follow routes don't have standard nav points — skip
                        // The route type (3=Follow) already indicates follow behavior
                    }
                    break;

                case "chk": // checkpoint — treat as point at 0,0,0
                case "jmp": // jump — treat as point at 0,0,0
                    break;
            }
        }

        // Insert point count after route type
        int pointCount = 0;
        for (int i = 0; i < navFileLines.Count; i++)
        {
            string l = navFileLines[i];
            if (l == "0" || l == "2" || l == "3" || l == "4" || l == "6")
            {
                // Count nav point type entries
                if (i == 0 || navFileLines[i - 1] == "0" || !int.TryParse(navFileLines[i - 1], out _))
                    pointCount++;
            }
        }

        // Actually count points more simply: each point starts with a type line
        // followed by EW, NS, Z, flag
        pointCount = CountNavPoints(navFileLines);

        navContent.Add(pointCount.ToString());
        navContent.AddRange(navFileLines);

        if (!string.IsNullOrEmpty(navName))
            navRoutes[navName] = navContent;
    }

    private static int CountNavPoints(List<string> navFileLines)
    {
        // Mirror NavRouteParser exactly via its shared trailer table so the two
        // counters cannot drift (the drift — type 6 counted as +12 here but read
        // as 6 trailer lines there — is what corrupted Portal routes pre-fix).
        int count = 0;
        int i = 0;
        while (i < navFileLines.Count)
        {
            if (!int.TryParse(navFileLines[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pointType))
                break; // desync / trailing data — stop counting
            if (!NavFile.IsKnownKind(pointType))
                break; // unknown type: trailer length unknown, stop (matches NavRouteParser)

            count++;
            i += 5 + NavFile.TrailerLineCount((WaypointKind)pointType); // type+EW+NS+Z+flag + trailer
        }
        return count;
    }

    /// <summary>
    /// Drops empty nav routes (header only, 0 points) from the dict and
    /// blanks EmbedNav action data on rules whose referenced nav was empty.
    /// Non-empty embedded navs survive in the dict for runtime + save.
    /// </summary>
    private static void PruneEmbeddedNavs(List<MetaRule> rules,
        Dictionary<string, List<string>> navRoutes)
    {
        if (navRoutes.Count == 0) return;

        var toRemove = new List<string>();
        foreach (var kvp in navRoutes)
        {
            var content = kvp.Value;
            if (content.Count <= 3 || content[2] == "0")
                toRemove.Add(kvp.Key);
        }
        foreach (string key in toRemove)
            navRoutes.Remove(key);

        ClearMissingEmbedRefs(rules, navRoutes);
    }

    private static void ClearMissingEmbedRefs(List<MetaRule> rules,
        Dictionary<string, List<string>> navRoutes)
    {
        foreach (var rule in rules)
        {
            if (rule.Action == MetaActionType.EmbeddedNavRoute &&
                !string.IsNullOrEmpty(rule.ActionData) &&
                !navRoutes.ContainsKey(rule.ActionData))
            {
                rule.ActionData = "";
            }

            if (rule.ActionChildren != null)
                ClearMissingEmbedRefs(rule.ActionChildren, navRoutes);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int CountLeadingTabs(string line)
    {
        int count = 0;
        foreach (char c in line)
        {
            if (c == '\t') count++;
            else break;
        }
        return count;
    }

    private static string StripPrefix(string text, string prefix)
    {
        text = text.Trim();
        if (text.StartsWith(prefix, StringComparison.Ordinal))
            text = text.Substring(prefix.Length);
        return text.TrimStart('\t', ' ');
    }

    /// <summary>
    /// Splits text into bare arguments (before first brace) and brace content.
    /// E.g. "0 {Portal Gem}" → bare="0", brace="Portal Gem"
    /// </summary>
    private static void SplitBareAndBrace(string text, out string bareArgs, out string braceContent)
    {
        int braceStart = text.IndexOf('{');
        if (braceStart < 0)
        {
            bareArgs = text.Trim();
            braceContent = "";
            return;
        }
        bareArgs = text.Substring(0, braceStart).Trim();
        braceContent = ExtractBraceContent(text.Substring(braceStart), out _);
    }

    /// <summary>
    /// Returns bare text (no braces) or brace content if braces exist.
    /// Handles both "4" and "{4}" formats.
    /// </summary>
    private static string ExtractBareOrBrace(string text)
    {
        text = text.Trim();
        if (text.StartsWith("{"))
            return ExtractBraceContent(text, out _);
        // Return the first token (up to space or end)
        int space = text.IndexOf(' ');
        return space > 0 ? text.Substring(0, space).Trim() : text;
    }

    /// <summary>
    /// Extracts content from the first {brace group} in the text.
    /// Returns the content inside braces and sets endPos to the position after the closing brace.
    /// </summary>
    private static string ExtractBraceContent(string text, out int endPos)
    {
        endPos = 0;
        int start = text.IndexOf('{');
        if (start < 0) return text.Trim();

        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    endPos = i + 1;
                    return text.Substring(start + 1, i - start - 1);
                }
            }
        }

        // Unmatched brace — return everything after the opening brace
        endPos = text.Length;
        return text.Substring(start + 1);
    }

    /// <summary>
    /// Splits a nav point line into tokens, treating {brace groups} as single tokens.
    /// </summary>
    private static string[] SplitNavTokens(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            // Skip whitespace
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length) break;

            if (line[i] == '{')
            {
                // Read brace-delimited token
                int depth = 0;
                int start = i + 1;
                for (; i < line.Length; i++)
                {
                    if (line[i] == '{') depth++;
                    else if (line[i] == '}')
                    {
                        depth--;
                        if (depth == 0) { i++; break; }
                    }
                }
                tokens.Add(line.Substring(start, Math.Max(0, i - start - 1)));
            }
            else
            {
                // Read space-delimited token
                int start = i;
                while (i < line.Length && line[i] != ' ' && line[i] != '\t' && line[i] != '{') i++;
                tokens.Add(line.Substring(start, i - start));
            }
        }
        return tokens.ToArray();
    }

    private static int RecallNameToSpellId(string name)
    {
        // Map common recall names to spell IDs
        return name.ToLowerInvariant() switch
        {
            "recall aphus lassel" or "aphus lassel recall" => 2931,
            "lifestone recall" or "lifestone" => 1635,
            "primary portal recall" or "primary portal" => 48,
            "secondary portal recall" or "secondary portal" => 2647,
            "portal recall" => 2645,
            "lifestone sending" or "lifestone tie" => 1635,
            "recall the sanctuary" or "sanctuary recall" => 2023,
            "call of the mhoire forge" or "mhoire forge" => 4213,
            "glenden wood recall" or "glenden wood" => 3865,
            "aerlinthe recall" or "aerlinthe" => 2041,
            "colosseum recall" or "colosseum" => 4084,
            "facility hub recall" or "facility hub" => 5541,
            "gear knight recall" or "gear knight" => 5542,
            "neftet recall" or "neftet" => 5543,
            "rynthid recall" or "rynthid" => 6321,
            "viridian rise recall" or "viridian rise" => 6322,
            _ => 0
        };
    }
}
