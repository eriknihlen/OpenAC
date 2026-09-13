using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AcDream.DrakBot.Meta;

/// <summary>
/// Parses VTank .met files into RynthAi MetaRule lists.
/// Uses byte-stream reading to correctly handle ba (byte array) sections
/// which are fixed-size byte regions that can end mid-line.
/// </summary>
public static class MetFileParser
{
    // ── VTank CType → RynthAi MetaConditionType ─────────────────────────────
    private static readonly MetaConditionType?[] VTankCTypeMap =
    {
        /*  0 */ MetaConditionType.Never,
        /*  1 */ MetaConditionType.Always,
        /*  2 */ MetaConditionType.All,
        /*  3 */ MetaConditionType.Any,
        /*  4 */ MetaConditionType.ChatMessage,
        /*  5 */ MetaConditionType.PackSlots_LE,
        /*  6 */ MetaConditionType.SecondsInState_GE,
        /*  7 */ MetaConditionType.NavrouteEmpty,
        /*  8 */ MetaConditionType.CharacterDeath,
        /*  9 */ MetaConditionType.AnyVendorOpen,
        /* 10 */ MetaConditionType.VendorClosed,
        /* 11 */ MetaConditionType.InventoryItemCount_LE,
        /* 12 */ MetaConditionType.InventoryItemCount_GE,
        /* 13 */ MetaConditionType.MonsterNameCountWithinDistance,
        /* 14 */ MetaConditionType.MonsterPriorityCountWithinDistance,
        /* 15 */ MetaConditionType.NeedToBuff,
        /* 16 */ MetaConditionType.NoMonstersWithinDistance,
        /* 17 */ MetaConditionType.Landblock_EQ,
        /* 18 */ MetaConditionType.Landcell_EQ,
        /* 19 */ MetaConditionType.PortalspaceEntered,
        /* 20 */ MetaConditionType.PortalspaceExited,
        /* 21 */ MetaConditionType.Not,
        /* 22 */ MetaConditionType.SecondsInStateP_GE,
        /* 23 */ MetaConditionType.TimeLeftOnSpell_GE,
        /* 24 */ MetaConditionType.BurdenPercentage_GE,
        /* 25 */ MetaConditionType.DistAnyRoutePT_GE,
        /* 26 */ MetaConditionType.Expression,
        /* 27 */ MetaConditionType.ChatMessageCapture,
    };

    // ── VTank AType → RynthAi MetaActionType ────────────────────────────────
    private static readonly MetaActionType?[] VTankATypeMap =
    {
        /*  0 */ MetaActionType.None,
        /*  1 */ MetaActionType.SetMetaState,
        /*  2 */ MetaActionType.ChatCommand,
        /*  3 */ MetaActionType.All,              // DoAll
        /*  4 */ MetaActionType.EmbeddedNavRoute,
        /*  5 */ MetaActionType.CallMetaState,
        /*  6 */ MetaActionType.ReturnFromCall,
        /*  7 */ MetaActionType.ExpressionAction,
        /*  8 */ MetaActionType.ChatExpression,
        /*  9 */ MetaActionType.SetWatchdog,
        /* 10 */ MetaActionType.ClearWatchdog,
        /* 11 */ MetaActionType.GetRAOption,
        /* 12 */ MetaActionType.SetRAOption,
        /* 13 */ MetaActionType.CreateView,
        /* 14 */ MetaActionType.DestroyView,
        /* 15 */ MetaActionType.DestroyAllViews,
    };

    private static MetaConditionType MapCType(int vtCType)
    {
        if (vtCType >= 0 && vtCType < VTankCTypeMap.Length && VTankCTypeMap[vtCType] is MetaConditionType ct)
            return ct;
        return MetaConditionType.Never;
    }

    private static MetaActionType MapAType(int vtAType)
    {
        if (vtAType >= 0 && vtAType < VTankATypeMap.Length && VTankATypeMap[vtAType] is MetaActionType at)
            return at;
        return MetaActionType.None;
    }

    // ── Byte-stream reader ──────────────────────────────────────────────────

    private sealed class MetReader
    {
        private readonly byte[] _data;
        private int _pos;

        public MetReader(byte[] data) { _data = data; _pos = 0; }
        public bool HasMore => _pos < _data.Length;

        /// <summary>Read one line up to \r\n or \n, advance past the line ending.</summary>
        public string ReadLine()
        {
            if (_pos >= _data.Length) return "";
            int start = _pos;
            while (_pos < _data.Length && _data[_pos] != (byte)'\n')
                _pos++;
            int end = _pos;
            if (_pos < _data.Length) _pos++; // skip \n
            if (end > start && _data[end - 1] == (byte)'\r')
                end--;
            return Encoding.UTF8.GetString(_data, start, end - start);
        }

        /// <summary>Peek at current line content without advancing position.</summary>
        public string PeekLine()
        {
            if (_pos >= _data.Length) return "";
            int p = _pos;
            while (p < _data.Length && _data[p] != (byte)'\n')
                p++;
            int end = p;
            if (end > _pos && _data[end - 1] == (byte)'\r')
                end--;
            return Encoding.UTF8.GetString(_data, _pos, end - _pos);
        }

        /// <summary>Skip exactly byteCount bytes (for ba sections).</summary>
        public void SkipBytes(int byteCount)
        {
            _pos = Math.Min(_pos + byteCount, _data.Length);
        }

        /// <summary>Read exactly byteCount bytes as UTF-8 string (for ba sections).</summary>
        public string ReadBytesAsString(int byteCount)
        {
            if (_pos >= _data.Length) return "";
            int count = Math.Min(byteCount, _data.Length - _pos);
            string result = Encoding.UTF8.GetString(_data, _pos, count);
            _pos += count;
            return result;
        }

        /// <summary>Read exactly byteCount bytes as lines (for nav extraction from ba).</summary>
        public List<string> ReadBytesAsLines(int byteCount)
        {
            if (_pos >= _data.Length) return new List<string>();
            int count = Math.Min(byteCount, _data.Length - _pos);
            int end = _pos + count;
            var lines = new List<string>();
            while (_pos < end)
            {
                int start = _pos;
                while (_pos < end && _data[_pos] != (byte)'\n')
                    _pos++;
                int lineEnd = _pos;
                if (_pos < end) _pos++; // skip \n
                if (lineEnd > start && _data[lineEnd - 1] == (byte)'\r')
                    lineEnd--;
                lines.Add(Encoding.UTF8.GetString(_data, start, lineEnd - start));
            }
            return lines;
        }
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public static LoadedMeta Load(string filePath)
    {
        if (!File.Exists(filePath)) return new LoadedMeta();
        return LoadFromBytes(File.ReadAllBytes(filePath));
    }

    public static LoadedMeta LoadFromBytes(byte[] data)
    {
        var result = new LoadedMeta();
        var reader = new MetReader(data);
        int navCounter = 0;

        try
        {
            result.Rules = ParseMetFile(reader, result.EmbeddedNavs, ref navCounter);
        }
        catch (Exception ex)
        {
            result.Rules = new List<MetaRule>();
            result.EmbeddedNavs.Clear();
            result.Warnings.Add($".met parse failed: {ex.GetType().Name}: {ex.Message}");
        }

        return result;
    }

    // ── File structure parsing ──────────────────────────────────────────────

    private static List<MetaRule> ParseMetFile(MetReader r,
        Dictionary<string, List<string>> navDict, ref int navCounter)
    {
        r.ReadLine(); // skip "1" (version)
        r.ReadLine(); // skip "CondAct" (table name)
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine(); // skip column names
        for (int i = 0; i < colCount; i++) r.ReadLine(); // skip column types

        int rowCount = ParseInt(r.ReadLine());
        if (rowCount <= 0) return new List<MetaRule>();

        var rules = new List<MetaRule>();
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            var rule = ParseRow(r, navDict, ref navCounter);
            if (rule != null)
                rules.Add(rule);
        }

        return rules;
    }

    private static MetaRule? ParseRow(MetReader r,
        Dictionary<string, List<string>> navDict, ref int navCounter)
    {
        if (!r.HasMore) return null;

        int vtCType = ReadTypedInt(r);
        MetaConditionType condition = MapCType(vtCType);

        int vtAType = ReadTypedInt(r);
        MetaActionType action = MapAType(vtAType);

        var rule = new MetaRule { Condition = condition, Action = action };
        ParseConditionData(rule, vtCType, r);
        ParseActionData(rule, vtAType, r, navDict, ref navCounter);

        rule.State = ReadTypedString(r);

        return rule;
    }

    // ── Condition data parsing ──────────────────────────────────────────────

    private static void ParseConditionData(MetaRule rule, int vtCType, MetReader r)
    {
        switch (vtCType)
        {
            case 2:  // All
            case 3:  // Any
            case 21: // Not
                ParseCompoundConditionTable(rule, r);
                break;

            case 26: // Expression
            case 27: // ChatMessageCapture
                rule.ConditionData = ReadExpressionTable(r);
                if (vtCType == 27 && string.IsNullOrEmpty(rule.ConditionData))
                    rule.ConditionData = ReadTypedString(r);
                break;

            case 4: // ChatMessage
                rule.ConditionData = ReadTypedString(r);
                break;

            case 11: // InventoryItemCount_LE
            case 12: // InventoryItemCount_GE
                ParseInventoryCountData(rule, r);
                break;

            case 13: // MonsterNameCountWithinDistance
                ParseMonsterNameCountData(rule, r);
                break;

            case 14: // MonsterPriorityCountWithinDistance
                ParseMonsterPriorityCountData(rule, r);
                break;

            case 23: // TimeLeftOnSpell_GE
                ParseTimeLeftOnSpellData(rule, r);
                break;

            case 17: // Landblock_EQ
            case 18: // Landcell_EQ
                ParseLandblockData(rule, r);
                break;

            case 16: // NoMonstersWithinDistance
            case 5:  // PackSlots_LE
            case 6:  // SecondsInState_GE
            case 22: // SecondsInStateP_GE
            case 24: // BurdenPercentage_GE
            case 25: // DistAnyRoutePT_GE
                rule.ConditionData = ReadNumericOrTable(r);
                break;

            case 0:  // Never
            case 1:  // Always
            case 7:  // NavEmpty
            case 8:  // CharacterDeath
            case 9:  // AnyVendorOpen
            case 10: // VendorClosed
            case 15: // NeedToBuff
            case 19: // PortalspaceEntered
            case 20: // PortalspaceExited
                SkipTypedValue(r);
                break;

            default:
                SkipTypedValue(r);
                break;
        }
    }

    private static void ParseCompoundConditionTable(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            SkipTypedValue(r);
            return;
        }

        r.ReadLine(); // skip "TABLE"
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column names
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column types
        int rowCount = ParseInt(r.ReadLine());

        rule.Children = new List<MetaRule>();
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            int childVtCType = ReadTypedInt(r);
            var child = new MetaRule
            {
                Condition = MapCType(childVtCType),
                State = rule.State
            };
            ParseConditionData(child, childVtCType, r);
            rule.Children.Add(child);
        }
    }

    private static string ReadExpressionTable(MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
            return ReadTypedString(r);

        r.ReadLine(); // skip "TABLE"
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column names
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column types
        int rowCount = ParseInt(r.ReadLine());

        string expression = "";
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            string k = ReadTypedString(r);
            string v = ReadTypedString(r);
            if (k == "e") expression = v;
        }
        return expression;
    }

    private static void ParseInventoryCountData(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "TABLE")
        {
            r.ReadLine(); // TABLE
            int colCount = ParseInt(r.ReadLine());
            for (int i = 0; i < colCount; i++) r.ReadLine();
            for (int i = 0; i < colCount; i++) r.ReadLine();
            int rowCount = ParseInt(r.ReadLine());

            string itemName = "";
            int count = 0;
            for (int row = 0; row < rowCount && r.HasMore; row++)
            {
                string k = ReadTypedString(r);
                if (k == "n") itemName = ReadTypedString(r);
                else if (k == "c")
                {
                    string vType = r.PeekLine().Trim();
                    count = vType == "d" ? (int)ReadTypedDouble(r) : ReadTypedInt(r);
                }
                else SkipTypedValue(r);
            }
            rule.ConditionData = $"{itemName},{count}";
        }
        else
        {
            rule.ConditionData = ReadTypedString(r);
        }
    }

    private static void ParseMonsterNameCountData(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "TABLE")
        {
            r.ReadLine(); // TABLE
            int colCount = ParseInt(r.ReadLine());
            for (int i = 0; i < colCount; i++) r.ReadLine();
            for (int i = 0; i < colCount; i++) r.ReadLine();
            int rowCount = ParseInt(r.ReadLine());

            string name = "";
            double dist = 0;
            int count = 0;
            for (int row = 0; row < rowCount && r.HasMore; row++)
            {
                string k = ReadTypedString(r);
                switch (k)
                {
                    case "n": name = ReadTypedString(r); break;
                    case "r":
                    {
                        string vType = r.PeekLine().Trim();
                        dist = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                        break;
                    }
                    case "c":
                    {
                        string vType = r.PeekLine().Trim();
                        count = vType == "d" ? (int)ReadTypedDouble(r) : ReadTypedInt(r);
                        break;
                    }
                    default: SkipTypedValue(r); break;
                }
            }
            rule.ConditionData = $"{name},{dist.ToString(CultureInfo.InvariantCulture)},{count}";
        }
        else
        {
            rule.ConditionData = ReadTypedString(r);
        }
    }

    private static void ParseMonsterPriorityCountData(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "TABLE")
        {
            r.ReadLine(); // TABLE
            int colCount = ParseInt(r.ReadLine());
            for (int i = 0; i < colCount; i++) r.ReadLine();
            for (int i = 0; i < colCount; i++) r.ReadLine();
            int rowCount = ParseInt(r.ReadLine());

            int count = 0;
            double dist = 0;
            for (int row = 0; row < rowCount && r.HasMore; row++)
            {
                string k = ReadTypedString(r);
                switch (k)
                {
                    case "r":
                    {
                        string vType = r.PeekLine().Trim();
                        dist = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                        break;
                    }
                    case "c":
                    {
                        string vType = r.PeekLine().Trim();
                        count = vType == "d" ? (int)ReadTypedDouble(r) : ReadTypedInt(r);
                        break;
                    }
                    default: SkipTypedValue(r); break;
                }
            }
            rule.ConditionData = $"{count},{dist.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            rule.ConditionData = ReadTypedString(r);
        }
    }

    private static void ParseTimeLeftOnSpellData(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "TABLE")
        {
            r.ReadLine(); // TABLE
            int colCount = ParseInt(r.ReadLine());
            for (int i = 0; i < colCount; i++) r.ReadLine();
            for (int i = 0; i < colCount; i++) r.ReadLine();
            int rowCount = ParseInt(r.ReadLine());

            uint spellId = 0;
            double seconds = 0;
            for (int row = 0; row < rowCount && r.HasMore; row++)
            {
                string k = ReadTypedString(r);
                switch (k)
                {
                    case "sid":
                    {
                        string vType = r.PeekLine().Trim();
                        spellId = (uint)(vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r));
                        break;
                    }
                    case "sec":
                    {
                        string vType = r.PeekLine().Trim();
                        seconds = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                        break;
                    }
                    default: SkipTypedValue(r); break;
                }
            }
            rule.ConditionData = $"{spellId},{seconds.ToString(CultureInfo.InvariantCulture)}";
        }
        else
        {
            string vType = r.PeekLine().Trim();
            if (vType == "d")
                rule.ConditionData = ReadTypedDouble(r).ToString(CultureInfo.InvariantCulture);
            else
                rule.ConditionData = ReadTypedInt(r).ToString();
        }
    }

    private static void ParseLandblockData(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "i")
        {
            int raw = ReadTypedInt(r);
            uint unsigned = unchecked((uint)raw);
            if (rule.Condition == MetaConditionType.Landblock_EQ)
                rule.ConditionData = (unsigned >> 16).ToString("X4");
            else
                rule.ConditionData = unsigned.ToString("X8");
        }
        else if (type == "d")
        {
            double d = ReadTypedDouble(r);
            uint unsigned = (uint)(long)d;
            if (rule.Condition == MetaConditionType.Landblock_EQ)
                rule.ConditionData = (unsigned >> 16).ToString("X4");
            else
                rule.ConditionData = unsigned.ToString("X8");
        }
        else
        {
            rule.ConditionData = ReadTypedString(r);
        }
    }

    // ── Action data parsing ─────────────────────────────────────────────────

    private static void ParseActionData(MetaRule rule, int vtAType, MetReader r,
        Dictionary<string, List<string>> navDict, ref int navCounter)
    {
        switch (vtAType)
        {
            case 3: // DoAll
                ParseDoAllTable(rule, r, navDict, ref navCounter);
                break;

            case 1: // SetMetaState
            case 5: // CallMetaState
                rule.ActionData = ReadTypedString(r);
                break;

            case 2: // ChatCommand
                rule.ActionData = ReadTypedString(r);
                break;

            case 4: // EmbedNav
                ParseEmbedNavData(rule, r, navDict, ref navCounter);
                break;

            case 7: // ExpressionAction
            case 8: // ChatExpression
                rule.ActionData = ReadExpressionTable(r);
                break;

            case 9: // SetWatchdog
                ParseSetWatchdogTable(rule, r);
                break;

            case 12: // SetRAOption
                ParseSetOptionTable(rule, r);
                break;

            case 11: // GetRAOption
                ParseGetOptionTable(rule, r);
                break;

            case 13: // CreateView
                ParseCreateViewTable(rule, r);
                break;

            case 14: // DestroyView
            {
                string type = r.PeekLine().Trim();
                if (type == "TABLE")
                    rule.ActionData = ReadExpressionTable(r);
                else
                    rule.ActionData = ReadTypedString(r);
                break;
            }

            case 0:  // None
            case 6:  // ReturnFromCall
            case 10: // ClearWatchdog
            case 15: // DestroyAllViews
                SkipTypedValue(r);
                break;

            default:
                SkipTypedValue(r);
                break;
        }
    }

    /// <summary>
    /// Reads a numeric value that may be stored as a simple typed value (i/d)
    /// or wrapped in a TABLE with key "r" (range), "c" (count), etc.
    /// </summary>
    private static string ReadNumericOrTable(MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type == "d")
            return ReadTypedDouble(r).ToString(CultureInfo.InvariantCulture);
        if (type == "i")
            return ReadTypedInt(r).ToString();
        if (type == "TABLE")
        {
            r.ReadLine(); // TABLE
            int colCount = ParseInt(r.ReadLine());
            for (int i = 0; i < colCount; i++) r.ReadLine();
            for (int i = 0; i < colCount; i++) r.ReadLine();
            int rowCount = ParseInt(r.ReadLine());

            double value = 0;
            for (int row = 0; row < rowCount && r.HasMore; row++)
            {
                string k = ReadTypedString(r);
                string vType = r.PeekLine().Trim();
                double v = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                // Use the first numeric value found (typically key "r" for range)
                if (row == 0) value = v;
            }
            return value.ToString(CultureInfo.InvariantCulture);
        }
        // Fallback
        SkipTypedValue(r);
        return "0";
    }

    private static void ParseDoAllTable(MetaRule rule, MetReader r,
        Dictionary<string, List<string>> navDict, ref int navCounter)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            SkipTypedValue(r);
            return;
        }

        r.ReadLine(); // TABLE
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine();
        for (int i = 0; i < colCount; i++) r.ReadLine();
        int rowCount = ParseInt(r.ReadLine());

        rule.ActionChildren = new List<MetaRule>();
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            int childVtAType = ReadTypedInt(r);
            var child = new MetaRule
            {
                Action = MapAType(childVtAType),
                State = rule.State
            };
            ParseActionData(child, childVtAType, r, navDict, ref navCounter);
            rule.ActionChildren.Add(child);
        }
    }

    private static void ParseEmbedNavData(MetaRule rule, MetReader r,
        Dictionary<string, List<string>> navDict, ref int navCounter)
    {
        string type = r.PeekLine().Trim();
        if (type != "ba")
        {
            SkipTypedValue(r);
            return;
        }

        r.ReadLine(); // skip "ba"
        int byteCount = ParseInt(r.ReadLine());

        // Read exactly byteCount bytes and split into lines
        var navLines = r.ReadBytesAsLines(byteCount);
        if (navLines.Count < 3) return;

        string navDisplayName = navLines[0]; // e.g. "[None]"
        int navStart = -1;
        for (int i = 1; i < navLines.Count; i++)
        {
            if (navLines[i].Contains("uTank2 NAV"))
            {
                navStart = i;
                break;
            }
        }
        if (navStart < 0) return;

        string cleanName = navDisplayName.Trim('[', ']', ' ');
        if (cleanName.EndsWith(".nav", StringComparison.OrdinalIgnoreCase))
            cleanName = cleanName.Substring(0, cleanName.Length - 4);
        cleanName = System.Text.RegularExpressions.Regex.Replace(cleanName, @"^nav\d+_+", "");

        string routeName;
        if (!string.IsNullOrEmpty(cleanName) &&
            !cleanName.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            routeName = SanitizeFileName(cleanName);
        }
        else
        {
            routeName = $"Route{navCounter++}";
        }

        var navContent = new List<string>();
        for (int i = navStart; i < navLines.Count; i++)
            navContent.Add(navLines[i]);

        // Skip empty routes (header only, 0 points)
        if (navContent.Count < 3) return;
        if (navContent.Count >= 3 && navContent[2] == "0") return;

        navDict[routeName] = navContent;
        rule.ActionData = routeName;
    }

    private static void ParseSetWatchdogTable(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            SkipTypedValue(r);
            return;
        }

        r.ReadLine(); // TABLE
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine();
        for (int i = 0; i < colCount; i++) r.ReadLine();
        int rowCount = ParseInt(r.ReadLine());

        string state = "Default";
        double range = 10;
        double time = 60;

        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            string k = ReadTypedString(r);
            switch (k)
            {
                case "s": state = ReadTypedString(r); break;
                case "r":
                {
                    string vType = r.PeekLine().Trim();
                    range = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                    break;
                }
                case "t":
                {
                    string vType = r.PeekLine().Trim();
                    time = vType == "d" ? ReadTypedDouble(r) : ReadTypedInt(r);
                    break;
                }
                default: SkipTypedValue(r); break;
            }
        }

        rule.ActionData = $"{state};{range.ToString(CultureInfo.InvariantCulture)};{time.ToString(CultureInfo.InvariantCulture)}";
    }

    private static void ParseSetOptionTable(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            rule.ActionData = ReadTypedString(r);
            return;
        }

        r.ReadLine(); // TABLE
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine();
        for (int i = 0; i < colCount; i++) r.ReadLine();
        int rowCount = ParseInt(r.ReadLine());

        string option = "";
        string value = "";
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            string k = ReadTypedString(r);
            switch (k)
            {
                case "o": option = ReadTypedString(r); break;
                case "v": value = ReadTypedString(r); break;
                default: SkipTypedValue(r); break;
            }
        }

        rule.ActionData = $"{option};{value}";
    }

    private static void ParseGetOptionTable(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            rule.ActionData = ReadTypedString(r);
            return;
        }

        r.ReadLine(); // TABLE
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine();
        for (int i = 0; i < colCount; i++) r.ReadLine();
        int rowCount = ParseInt(r.ReadLine());

        string variable = "";
        string option = "";
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            string k = ReadTypedString(r);
            switch (k)
            {
                case "v": variable = ReadTypedString(r); break;
                case "o": option = ReadTypedString(r); break;
                default: SkipTypedValue(r); break;
            }
        }

        rule.ActionData = $"{variable};{option}";
    }

    private static void ParseCreateViewTable(MetaRule rule, MetReader r)
    {
        string type = r.PeekLine().Trim();
        if (type != "TABLE")
        {
            SkipTypedValue(r);
            return;
        }

        r.ReadLine(); // TABLE
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine();
        for (int i = 0; i < colCount; i++) r.ReadLine();
        int rowCount = ParseInt(r.ReadLine());

        string viewName = "";
        string viewXml = "";
        for (int row = 0; row < rowCount && r.HasMore; row++)
        {
            string k = ReadTypedString(r);
            switch (k)
            {
                case "n": viewName = ReadTypedString(r); break;
                case "x":
                {
                    string vType = r.PeekLine().Trim();
                    if (vType == "ba")
                    {
                        viewXml = ReadByteArrayAsString(r);
                    }
                    else
                    {
                        viewXml = ReadTypedString(r);
                    }
                    break;
                }
                default: SkipTypedValue(r); break;
            }
        }

        rule.ActionData = $"{viewName};{viewXml}";
    }

    // ── Low-level value readers ─────────────────────────────────────────────

    private static int ParseInt(string s)
    {
        int.TryParse(s.Trim(), out int val);
        return val;
    }

    private static int ReadTypedInt(MetReader r)
    {
        if (!r.HasMore) return 0;
        string type = r.ReadLine().Trim();
        if (type == "n" || !r.HasMore) return 0;
        if (type == "i")
        {
            int.TryParse(r.ReadLine().Trim(), out int val);
            return val;
        }
        if (type == "d")
        {
            double.TryParse(r.ReadLine().Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d);
            return (int)d;
        }
        r.ReadLine(); // unknown type — skip one value line
        return 0;
    }

    private static double ReadTypedDouble(MetReader r)
    {
        if (!r.HasMore) return 0;
        string type = r.ReadLine().Trim();
        if (type == "n" || !r.HasMore) return 0;
        if (type == "d" || type == "i")
        {
            double.TryParse(r.ReadLine().Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double val);
            return val;
        }
        r.ReadLine();
        return 0;
    }

    private static string ReadTypedString(MetReader r)
    {
        if (!r.HasMore) return "";
        string type = r.ReadLine().Trim();
        if (type == "n") return "";
        if (!r.HasMore) return "";

        if (type == "s")
            return r.ReadLine();
        if (type == "i" || type == "d")
            return r.ReadLine().Trim();
        if (type == "ba")
        {
            int byteCount = ParseInt(r.ReadLine());
            return r.ReadBytesAsString(byteCount);
        }
        if (type == "TABLE")
        {
            // Skip through the table — back up by re-parsing
            SkipTableBody(r);
            return "";
        }

        r.ReadLine(); // unknown type
        return "";
    }

    private static string ReadByteArrayAsString(MetReader r)
    {
        if (!r.HasMore) return "";
        string type = r.ReadLine().Trim();
        if (type != "ba") return "";
        if (!r.HasMore) return "";

        int byteCount = ParseInt(r.ReadLine());
        return r.ReadBytesAsString(byteCount);
    }

    private static void SkipTypedValue(MetReader r)
    {
        if (!r.HasMore) return;
        string type = r.ReadLine().Trim();
        if (type == "n") return;
        if (type == "i" || type == "s" || type == "d")
        {
            r.ReadLine(); // skip value line
            return;
        }
        if (type == "ba")
        {
            int byteCount = ParseInt(r.ReadLine());
            r.SkipBytes(byteCount);
            return;
        }
        if (type == "TABLE")
        {
            SkipTableBody(r);
            return;
        }
        // Unknown type — try skipping one line
        if (r.HasMore) r.ReadLine();
    }

    /// <summary>Skip a TABLE body (called after the "TABLE" line has already been consumed).</summary>
    private static void SkipTableBody(MetReader r)
    {
        int colCount = ParseInt(r.ReadLine());
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column names
        for (int i = 0; i < colCount; i++) r.ReadLine(); // column types
        int rowCount = ParseInt(r.ReadLine());

        for (int row = 0; row < rowCount; row++)
        {
            for (int c = 0; c < colCount; c++)
                SkipTypedValue(r);
        }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var result = new char[name.Length];
        for (int i = 0; i < name.Length; i++)
            result[i] = Array.IndexOf(invalid, name[i]) >= 0 ? '_' : name[i];
        return new string(result);
    }
}
