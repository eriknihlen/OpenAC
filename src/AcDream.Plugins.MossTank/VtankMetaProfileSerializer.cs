using System.Globalization;
using AcDream.Plugin.Abstractions;

namespace AcDream.Plugins.MossTank;

internal static class VtankMetaProfileSerializer
{
    private static readonly string[] Header =
    [
        "1", "CondAct", "5", "CType", "AType", "CData", "AData",
        "State", "n", "n", "n", "n", "n",
    ];

    private static readonly string[] TablePrefix = ["TABLE", "2", "k", "v", "n", "n"];
    private static readonly string[] RecursiveTablePrefix = ["TABLE", "2", "K", "V", "n", "n"];
    private const int MaximumRules = 100_000;
    private const int MaximumNesting = 256;

    public static bool TryLoad(string source, out MetaProfile profile, out string error) =>
        TryLoad(source, MetafSerializer.NoOpSpells.Instance, out profile, out error);

    public static bool TryLoad(
        string source,
        ISpellCatalog spells,
        out MetaProfile profile,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(spells);
        try
        {
            var reader = new LineReader(source);
            reader.Expect(Header);
            int count = reader.ReadInt();
            if (count is < 0 or > MaximumRules)
                throw reader.Error("Invalid VTank Meta rule count.");

            var parsed = new MetaProfile();
            for (int index = 0; index < count; index++)
            {
                reader.Expect("i");
                int conditionType = reader.ReadInt();
                reader.Expect("i");
                int actionType = reader.ReadInt();
                MetaCondition condition = ReadCondition(reader, conditionType, 0);
                MetaAction action = ReadAction(reader, actionType, 0, spells);
                reader.Expect("s");
                parsed.Rules.Add(new MetaRule
                {
                    State = reader.Read(),
                    Condition = condition,
                    Action = action,
                    Enabled = true,
                });
            }
            reader.ExpectEnd();
            profile = parsed;
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException
            or OverflowException or ArgumentOutOfRangeException)
        {
            profile = new MetaProfile();
            error = exception.Message;
            return false;
        }
    }

    private static MetaCondition ReadCondition(LineReader reader, int type, int depth)
    {
        CheckDepth(reader, depth);
        var value = new MetaCondition { Kind = ConditionKind(type) };
        switch (type)
        {
            case 0 or 1 or 7 or 8 or 9 or 10 or 15 or 19 or 20:
                reader.Expect("i", "0");
                break;
            case 2 or 3:
                reader.Expect(RecursiveTablePrefix);
                ReadConditions(reader, value, reader.ReadCount(), depth);
                break;
            case 4:
                reader.Expect("s");
                value.Text = reader.Read();
                break;
            case 5 or 6 or 17 or 18 or 22 or 24:
                reader.Expect("i");
                value.Number = reader.ReadInt();
                break;
            case 11 or 12:
                reader.Expect(TablePrefix, "2", "s", "n", "s");
                value.Text = reader.Read();
                reader.Expect("s", "c", "i");
                value.Number = reader.ReadInt();
                break;
            case 13:
                reader.Expect(TablePrefix, "3", "s", "n", "s");
                value.Text = reader.Read();
                reader.Expect("s", "c", "i");
                value.Number = reader.ReadInt();
                reader.Expect("s", "r", "d");
                value.SecondaryNumber = reader.ReadDouble();
                break;
            case 14:
                reader.Expect(TablePrefix, "3", "s", "p", "i");
                value.TertiaryNumber = reader.ReadInt();
                reader.Expect("s", "c", "i");
                value.Number = reader.ReadInt();
                reader.Expect("s", "r", "d");
                value.SecondaryNumber = reader.ReadDouble();
                break;
            case 16:
                reader.Expect(TablePrefix, "1", "s", "r", "d");
                value.Number = reader.ReadDouble();
                break;
            case 21:
                reader.Expect(RecursiveTablePrefix);
                if (reader.ReadCount() != 1)
                    throw reader.Error("VTank Meta Not requires exactly one condition.");
                reader.Expect("i");
                value.Children.Add(ReadCondition(reader, reader.ReadInt(), depth + 1));
                break;
            case 23:
                reader.Expect(TablePrefix, "2", "s", "sid", "i");
                value.Number = reader.ReadInt();
                reader.Expect("s", "sec", "i");
                value.SecondaryNumber = reader.ReadInt();
                break;
            case 25:
                reader.Expect(TablePrefix, "1", "s", "dist", "d");
                value.Number = reader.ReadDouble();
                break;
            case 26:
                reader.Expect(TablePrefix, "1", "s", "e", "s");
                value.Text = reader.Read();
                break;
            case 28:
                reader.Expect(TablePrefix, "2", "s", "p", "s");
                value.Text = reader.Read();
                reader.Expect("s", "c", "s");
                value.SecondaryText = reader.Read();
                break;
            default:
                throw reader.Error($"Unknown VTank Meta condition type {type}.");
        }
        return value;
    }

    private static void ReadConditions(
        LineReader reader,
        MetaCondition target,
        int count,
        int depth)
    {
        for (int index = 0; index < count; index++)
        {
            reader.Expect("i");
            target.Children.Add(ReadCondition(reader, reader.ReadInt(), depth + 1));
        }
    }

    private static MetaAction ReadAction(LineReader reader, int type, int depth, ISpellCatalog spells)
    {
        CheckDepth(reader, depth);
        var value = new MetaAction { Kind = ActionKind(type) };
        switch (type)
        {
            case 0 or 6:
                reader.Expect("i", "0");
                break;
            case 1 or 2:
                reader.Expect("s");
                value.Text = reader.Read();
                break;
            case 3:
                reader.Expect(RecursiveTablePrefix);
                int count = reader.ReadCount();
                for (int index = 0; index < count; index++)
                {
                    reader.Expect("i");
                    value.Children.Add(ReadAction(reader, reader.ReadInt(), depth + 1, spells));
                }
                break;
            case 4:
                ReadEmbeddedNavigation(reader, value, spells);
                break;
            case 5:
                reader.Expect(TablePrefix, "2", "s", "st", "s");
                value.Text = reader.Read();
                reader.Expect("s", "ret", "s");
                value.SecondaryText = reader.Read();
                break;
            case 7 or 8:
                reader.Expect(TablePrefix, "1", "s", "e", "s");
                value.Text = reader.Read();
                break;
            case 9:
                reader.Expect(TablePrefix, "3", "s", "s", "s");
                value.Text = reader.Read();
                reader.Expect("s", "r", "d");
                value.Number = reader.ReadDouble();
                reader.Expect("s", "t", "d");
                value.SecondaryNumber = reader.ReadDouble();
                break;
            case 10 or 15:
                reader.Expect(TablePrefix, "0");
                break;
            case 11:
                reader.Expect(TablePrefix, "2", "s", "o", "s");
                value.Text = reader.Read();
                reader.Expect("s", "v", "s");
                value.SecondaryText = reader.Read();
                break;
            case 12:
                reader.Expect(TablePrefix, "2", "s", "o", "s");
                value.Text = reader.Read();
                reader.Expect("s", "v", "s");
                value.SecondaryText = reader.Read();
                break;
            case 13:
                reader.Expect(TablePrefix, "2", "s", "n", "s");
                value.Text = reader.Read();
                reader.Expect("s", "x", "ba");
                int length = reader.ReadCount();
                value.SecondaryText = reader.ReadByteArray(length);
                break;
            case 14:
                reader.Expect(TablePrefix, "1", "s", "n", "s");
                value.Text = reader.Read();
                break;
            default:
                throw reader.Error($"Unknown VTank Meta action type {type}.");
        }
        return value;
    }

    private static void ReadEmbeddedNavigation(LineReader reader, MetaAction target, ISpellCatalog spells)
    {
        reader.Expect("ba");
        int serializedCharacters = reader.ReadCount();
        target.SecondaryText = reader.Read();
        int statedNodeCount = reader.ReadCount();
        if (serializedCharacters <= 5)
        {
            target.EmbeddedRoute = new NavigationSettings();
            return;
        }

        var lines = new List<string> { reader.ReadExpected("uTank2 NAV 1.2") };
        int mode = reader.ReadInt(out string modeLine);
        lines.Add(modeLine);
        int actualNodeCount;
        if (mode == 3)
        {
            lines.Add(reader.Read());
            lines.Add(reader.Read());
            actualNodeCount = 1;
        }
        else if (mode is 1 or 2 or 4)
        {
            actualNodeCount = reader.ReadCount(out string countLine);
            lines.Add(countLine);
            for (int index = 0; index < actualNodeCount; index++)
                ReadNavigationNode(reader, lines);
        }
        else
        {
            throw reader.Error($"Unknown embedded VTank navigation type {mode}.");
        }
        if (actualNodeCount != statedNodeCount)
            throw reader.Error("Embedded VTank navigation node counts do not match.");

        string blob = string.Join("\r\n", lines) + "\r\n";
        var route = new NavigationSettings();
        if (!VtankNavRouteSerializer.TryLoad(blob, route, spells, out string navError))
            throw reader.Error($"Embedded VTank navigation route is invalid: {navError}");
        target.EmbeddedRoute = route;
    }

    private static void ReadNavigationNode(LineReader reader, List<string> lines)
    {
        int type = reader.ReadInt(out string typeLine);
        lines.Add(typeLine);
        for (int index = 0; index < 4; index++)
            lines.Add(reader.Read());
        int extra = type switch
        {
            0 or 8 => 0,
            1 or 2 or 3 or 4 => 1,
            5 => 2,
            6 or 7 => 6,
            9 => 3,
            _ => throw reader.Error($"Unknown embedded VTank waypoint type {type}."),
        };
        for (int index = 0; index < extra; index++)
            lines.Add(reader.Read());
    }

    private static string NormalizeNewlines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private static MetaConditionKind ConditionKind(int type) => type switch
    {
        0 => MetaConditionKind.Never,
        1 => MetaConditionKind.Always,
        2 => MetaConditionKind.All,
        3 => MetaConditionKind.Any,
        4 => MetaConditionKind.ChatMessage,
        5 => MetaConditionKind.PackSlotsLessThanOrEqual,
        6 => MetaConditionKind.SecondsInStateGreaterThanOrEqual,
        7 => MetaConditionKind.NavigationRouteEmpty,
        8 => MetaConditionKind.CharacterDeath,
        9 => MetaConditionKind.AnyVendorOpen,
        10 => MetaConditionKind.VendorClosed,
        11 => MetaConditionKind.InventoryItemCountLessThanOrEqual,
        12 => MetaConditionKind.InventoryItemCountGreaterThanOrEqual,
        13 => MetaConditionKind.MonsterNameCountWithinDistance,
        14 => MetaConditionKind.MonsterPriorityCountWithinDistance,
        15 => MetaConditionKind.NeedToBuff,
        16 => MetaConditionKind.NoMonstersWithinDistance,
        17 => MetaConditionKind.LandblockEquals,
        18 => MetaConditionKind.LandcellEquals,
        19 => MetaConditionKind.PortalspaceEntered,
        20 => MetaConditionKind.PortalspaceExited,
        21 => MetaConditionKind.Not,
        22 => MetaConditionKind.PersistentSecondsInStateGreaterThanOrEqual,
        23 => MetaConditionKind.TimeLeftOnSpellGreaterThanOrEqual,
        24 => MetaConditionKind.BurdenPercentGreaterThanOrEqual,
        25 => MetaConditionKind.DistanceFromAnyRoutePointGreaterThanOrEqual,
        26 => MetaConditionKind.Expression,
        28 => MetaConditionKind.ChatMessageCapture,
        _ => throw new FormatException($"Unknown VTank Meta condition type {type}."),
    };

    private static MetaActionKind ActionKind(int type) => type switch
    {
        0 => MetaActionKind.None,
        1 => MetaActionKind.SetMetaState,
        2 => MetaActionKind.ChatCommand,
        3 => MetaActionKind.All,
        4 => MetaActionKind.LoadEmbeddedNavigationRoute,
        5 => MetaActionKind.CallMetaState,
        6 => MetaActionKind.ReturnFromCall,
        7 => MetaActionKind.ExpressionAction,
        8 => MetaActionKind.ChatExpression,
        9 => MetaActionKind.SetWatchdog,
        10 => MetaActionKind.ClearWatchdog,
        11 => MetaActionKind.GetVtankOption,
        12 => MetaActionKind.SetVtankOption,
        13 => MetaActionKind.CreateView,
        14 => MetaActionKind.DestroyView,
        15 => MetaActionKind.DestroyAllViews,
        _ => throw new FormatException($"Unknown VTank Meta action type {type}."),
    };

    private static void CheckDepth(LineReader reader, int depth)
    {
        if (depth > MaximumNesting)
            throw reader.Error("VTank Meta nesting is too deep.");
    }

    private sealed class LineReader
    {
        private readonly List<string> _lines;
        private int _index;

        public LineReader(string source)
        {
            string normalized = NormalizeNewlines(source ?? string.Empty);
            _lines = normalized.Split('\n', StringSplitOptions.None).ToList();
            if (_lines.Count != 0 && _lines[^1].Length == 0)
                _lines.RemoveAt(_lines.Count - 1);
        }

        public string Read()
        {
            if (_index >= _lines.Count)
                throw Error("Unexpected end of VTank Meta data.");
            return _lines[_index++];
        }

        public string ReadExpected(string expected)
        {
            string actual = Read();
            if (!actual.Equals(expected, StringComparison.Ordinal))
                throw Error($"Expected '{expected}', found '{actual}'.");
            return actual;
        }

        public void Expect(params string[] expected)
        {
            foreach (string value in expected)
                ReadExpected(value);
        }

        public void Expect(string[] first, params string[] rest)
        {
            Expect(first);
            Expect(rest);
        }

        public int ReadInt() => int.Parse(
            Read(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        public int ReadInt(out string line)
        {
            line = Read();
            return int.Parse(line, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        public int ReadCount()
        {
            int value = ReadInt();
            if (value is < 0 or > MaximumRules)
                throw Error("Invalid VTank Meta collection count.");
            return value;
        }

        public int ReadCount(out string line)
        {
            int value = ReadInt(out line);
            if (value is < 0 or > MaximumRules)
                throw Error("Invalid VTank Meta collection count.");
            return value;
        }

        public double ReadDouble() => double.Parse(
            Read(), NumberStyles.Float, CultureInfo.InvariantCulture);

        public string ReadByteArray(int length)
        {
            if (length < 0)
                throw Error("Invalid VTank Meta byte-array length.");
            string first = Read();
            if (first.Length >= length)
            {
                string value = first[..length];
                string remainder = first[length..];
                if (remainder.Length != 0)
                    _lines.Insert(_index, remainder);
                return value;
            }

            var valueBuilder = new System.Text.StringBuilder(first);
            while (valueBuilder.Length < length && _index < _lines.Count)
            {
                valueBuilder.Append("\r\n");
                valueBuilder.Append(Read());
            }
            if (valueBuilder.Length < length)
                throw Error("Truncated VTank Meta byte array.");
            string combined = valueBuilder.ToString();
            string result = combined[..length];
            string remaining = combined[length..];
            if (remaining.Length != 0)
                _lines.Insert(_index, remaining.TrimStart('\r', '\n'));
            return result;
        }

        public void ExpectEnd()
        {
            while (_index < _lines.Count && _lines[_index].Length == 0)
                _index++;
            if (_index != _lines.Count)
                throw Error($"Unexpected trailing VTank Meta data '{_lines[_index]}'.");
        }

        public FormatException Error(string message) =>
            new($"VTank Meta line {Math.Min(_index + 1, _lines.Count + 1)}: {message}");
    }
}
