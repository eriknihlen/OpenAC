using System.Globalization;
using System.Text;

namespace AcDream.Plugins.MossTank;

internal sealed class VtankLootRequirement
{
    public int Type { get; set; }
    public string Payload { get; set; } = string.Empty;
}

internal sealed class VtankSalvageCombineSettings
{
    public string DefaultCombineString { get; set; } = "1-6, 7-8, 9, 10";
    public Dictionary<int, string> MaterialCombineStrings { get; set; } =
        CreateVtankDefaults();
    public Dictionary<int, int> MaterialValueModeValues { get; set; } = [];

    public VtankSalvageCombineSettings Clone() => new()
    {
        DefaultCombineString = DefaultCombineString,
        MaterialCombineStrings = new Dictionary<int, string>(
            MaterialCombineStrings),
        MaterialValueModeValues = new Dictionary<int, int>(
            MaterialValueModeValues),
    };

    private static Dictionary<int, string> CreateVtankDefaults()
    {
        const string oneThroughTen = "1-10";
        int[] materials =
        [
            10, 14, 16, 17, 18, 19, 22, 25, 29, 30, 36, 37, 41,
            47, 35, 27, 26, 21, 15, 13,
            50, 49, 34,
            52, 51,
        ];
        return materials.ToDictionary(
            static material => material,
            static _ => oneThroughTen);
    }
}

internal sealed class VtankLootExtraBlock
{
    public string Type { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
}

internal sealed class VtankLootProfile
{
    public int SourceVersion { get; set; } = 1;
    public List<LootRule> Rules { get; set; } = [];
    public VtankSalvageCombineSettings SalvageCombine { get; set; } = new();
    public List<VtankLootExtraBlock> UnknownBlocks { get; set; } = [];
}

internal static class VtankLootProfileSerializer
{
    private const string Header = "UTL";
    private const int CurrentVersion = 1;
    private const string SalvageBlock = "SalvageCombine";
    private const int DisabledRuleType = 9999;
    private static readonly string NewLine = "\r\n";

    public static bool TryRead(
        string? source,
        out VtankLootProfile profile,
        out string error)
    {
        profile = new VtankLootProfile();
        error = string.Empty;
        if (string.IsNullOrEmpty(source))
        {
            error = "The VTClassic loot profile is empty.";
            return false;
        }

        try
        {
            var reader = new CharacterReader(source);
            string first = reader.ReadLine();
            int version;
            int count;
            if (string.Equals(first, Header, StringComparison.Ordinal))
            {
                version = ParseInt(reader.ReadLine(), "profile version");
                if (version is < 0 or > CurrentVersion)
                    throw new FormatException(
                        $"VTClassic loot profile version {version} is not supported.");
                count = ParseCount(reader.ReadLine(), "rule count", 100_000);
            }
            else
            {
                version = 0;
                count = ParseCount(first, "rule count", 100_000);
            }

            profile.SourceVersion = version;
            for (int index = 0; index < count; index++)
                profile.Rules.Add(ReadRule(reader, version, index));

            while (!reader.End)
            {
                string blockType = reader.ReadLine();
                if (blockType.Length == 0 && reader.End)
                    break;
                int length = ParseCount(
                    reader.ReadLine(),
                    $"{blockType} block length",
                    16 * 1024 * 1024);
                string payload = reader.ReadCharacters(length);
                if (string.Equals(
                    blockType,
                    SalvageBlock,
                    StringComparison.Ordinal))
                {
                    profile.SalvageCombine = ReadSalvage(payload);
                }
                else
                {
                    profile.UnknownBlocks.Add(new VtankLootExtraBlock
                    {
                        Type = blockType,
                        Payload = payload,
                    });
                }
            }
            return true;
        }
        catch (FormatException failure)
        {
            profile = new VtankLootProfile();
            error = failure.Message;
            return false;
        }
    }

    public static string Write(VtankLootProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var output = new StringBuilder();
        AppendLine(output, Header);
        AppendLine(output, CurrentVersion);
        AppendLine(output, profile.Rules.Count);
        foreach (LootRule rule in profile.Rules)
            WriteRule(output, rule);

        WriteBlock(output, SalvageBlock, WriteSalvage(profile.SalvageCombine));
        foreach (VtankLootExtraBlock block in profile.UnknownBlocks)
        {
            if (string.IsNullOrEmpty(block.Type)
                || string.Equals(
                    block.Type,
                    SalvageBlock,
                    StringComparison.Ordinal))
            {
                continue;
            }
            WriteBlock(output, block.Type, block.Payload ?? string.Empty);
        }
        return output.ToString();
    }

    private static LootRule ReadRule(
        CharacterReader reader,
        int version,
        int ruleIndex)
    {
        string name = reader.ReadLine();
        string customExpression = version >= 1
            ? reader.ReadLine()
            : string.Empty;
        string[] fields = reader.ReadLine().Split(';');
        if (fields.Length < 2)
            throw new FormatException($"Loot rule {ruleIndex + 1} has an invalid header.");
        int priority = ParseInt(fields[0], $"rule {ruleIndex + 1} priority");
        int actionValue = ParseInt(fields[1], $"rule {ruleIndex + 1} action");
        if (actionValue is < 0 or > 10)
            throw new FormatException($"Loot rule {ruleIndex + 1} has action {actionValue}.");

        var rule = new LootRule
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Rule {ruleIndex + 1}" : name,
            Expression = "*",
            CustomExpression = customExpression,
            Action = (LootAction)actionValue,
            Priority = priority,
        };
        if (rule.Action == LootAction.KeepUpTo)
        {
            rule.KeepCount = Math.Max(
                0,
                ParseInt(reader.ReadLine(), $"rule {ruleIndex + 1} keep count"));
        }

        for (int field = 2; field < fields.Length; field++)
        {
            int type = ParseInt(
                fields[field],
                $"rule {ruleIndex + 1} requirement type");
            string payload;
            if (version >= 1)
            {
                int length = ParseCount(
                    reader.ReadLine(),
                    $"rule {ruleIndex + 1} requirement length",
                    16 * 1024 * 1024);
                payload = reader.ReadCharacters(length);
            }
            else
            {
                int lines = LegacyPayloadLineCount(type);
                if (lines < 0)
                    throw new FormatException(
                        $"Version 0 loot rule {ruleIndex + 1} uses unknown requirement {type}.");
                var legacy = new StringBuilder();
                for (int line = 0; line < lines; line++)
                    AppendLine(legacy, reader.ReadLine());
                payload = legacy.ToString();
            }
            rule.VtankRequirements.Add(new VtankLootRequirement
            {
                Type = type,
                Payload = payload,
            });
        }
        return rule;
    }

    private static void WriteRule(StringBuilder output, LootRule rule)
    {
        AppendLine(output, SingleLine(rule.Name, "Rule"));
        AppendLine(output, SingleLine(rule.CustomExpression, string.Empty));

        IReadOnlyList<VtankLootRequirement> requirements =
            ExportRequirements(rule);
        var header = new StringBuilder();
        header.Append(rule.Priority.ToString(CultureInfo.InvariantCulture));
        header.Append(';');
        int action = (int)rule.Action is >= 0 and <= 10
            ? (int)rule.Action
            : (int)LootAction.NoLoot;
        header.Append(action.ToString(CultureInfo.InvariantCulture));
        foreach (VtankLootRequirement requirement in requirements)
        {
            header.Append(';');
            header.Append(requirement.Type.ToString(CultureInfo.InvariantCulture));
        }
        AppendLine(output, header.ToString());

        if (action == (int)LootAction.KeepUpTo)
            AppendLine(output, Math.Max(0, rule.KeepCount));
        foreach (VtankLootRequirement requirement in requirements)
        {
            string payload = NormalizePayload(requirement.Payload);
            AppendLine(output, payload.Length);
            output.Append(payload);
        }
    }

    private static IReadOnlyList<VtankLootRequirement> ExportRequirements(
        LootRule rule)
    {
        if (rule.VtankRequirements.Count > 0)
            return rule.VtankRequirements;

        return
        [
            new VtankLootRequirement
            {
                Type = DisabledRuleType,
                Payload = "true" + NewLine,
            },
        ];
    }

    private static VtankSalvageCombineSettings ReadSalvage(string payload)
    {
        var reader = new CharacterReader(payload);
        _ = ParseInt(reader.ReadLine(), "salvage block version");
        var result = new VtankSalvageCombineSettings
        {
            DefaultCombineString = reader.ReadLine(),
            MaterialCombineStrings = [],
            MaterialValueModeValues = [],
        };
        int strings = ParseCount(
            reader.ReadLine(),
            "salvage material rule count",
            10_000);
        for (int index = 0; index < strings; index++)
        {
            int material = ParseInt(reader.ReadLine(), "salvage material id");
            result.MaterialCombineStrings[material] = reader.ReadLine();
        }
        if (reader.End)
            return result;
        int values = ParseCount(
            reader.ReadLine(),
            "salvage value-mode count",
            10_000);
        for (int index = 0; index < values; index++)
        {
            int material = ParseInt(reader.ReadLine(), "salvage value material id");
            result.MaterialValueModeValues[material] = ParseInt(
                reader.ReadLine(),
                "salvage value-mode value");
        }
        return result;
    }

    private static string WriteSalvage(VtankSalvageCombineSettings? settings)
    {
        settings ??= new VtankSalvageCombineSettings();
        var output = new StringBuilder();
        AppendLine(output, 1);
        AppendLine(output, SingleLine(
            settings.DefaultCombineString,
            "1-6, 7-8, 9, 10"));
        AppendLine(output, settings.MaterialCombineStrings.Count);
        foreach ((int material, string combine) in
            settings.MaterialCombineStrings.OrderBy(static pair => pair.Key))
        {
            AppendLine(output, material);
            AppendLine(output, SingleLine(combine, string.Empty));
        }
        AppendLine(output, settings.MaterialValueModeValues.Count);
        foreach ((int material, int value) in
            settings.MaterialValueModeValues.OrderBy(static pair => pair.Key))
        {
            AppendLine(output, material);
            AppendLine(output, value);
        }
        return output.ToString();
    }

    private static void WriteBlock(
        StringBuilder output,
        string type,
        string payload)
    {
        string normalized = NormalizePayload(payload);
        AppendLine(output, SingleLine(type, "Unknown"));
        AppendLine(output, normalized.Length);
        output.Append(normalized);
    }

    private static string NormalizePayload(string? payload) =>
        (payload ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", NewLine, StringComparison.Ordinal);

    private static string SingleLine(string? value, string fallback)
    {
        string normalized = value ?? fallback;
        int lineEnd = normalized.IndexOfAny(['\r', '\n']);
        return lineEnd < 0 ? normalized : normalized[..lineEnd];
    }

    private static int LegacyPayloadLineCount(int type) => type switch
    {
        0 => 1,
        1 => 2,
        2 or 3 or 4 or 5 or 11 or 12 or 13 or 2003 or 2005 => 2,
        6 or 7 or 8 or 10 or 1001 or 1002 or 1003 or 2000 or 2001
            or 2006 or 2007 or 9999 => 1,
        9 or 1004 or 2008 => 3,
        14 => 5,
        15 or 16 => 6,
        17 or 1000 => 2,
        _ => -1,
    };

    private static int ParseCount(string value, string field, int maximum)
    {
        int parsed = ParseInt(value, field);
        if (parsed < 0 || parsed > maximum)
            throw new FormatException($"Invalid {field}: {value}.");
        return parsed;
    }

    private static int ParseInt(string value, string field) =>
        int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsed)
                ? parsed
                : throw new FormatException($"Invalid {field}: {value}.");

    private static void AppendLine(StringBuilder output, string value) =>
        output.Append(value).Append(NewLine);

    private static void AppendLine(StringBuilder output, int value) =>
        AppendLine(output, value.ToString(CultureInfo.InvariantCulture));

    private sealed class CharacterReader(string source)
    {
        private int _position;

        public bool End => _position >= source.Length;

        public string ReadLine()
        {
            if (End)
                throw new FormatException("The VTClassic loot profile ended unexpectedly.");
            int start = _position;
            while (_position < source.Length
                && source[_position] is not ('\r' or '\n'))
            {
                _position++;
            }
            string line = source[start.._position];
            if (_position < source.Length && source[_position] == '\r')
                _position++;
            if (_position < source.Length && source[_position] == '\n')
                _position++;
            return line;
        }

        public string ReadCharacters(int count)
        {
            if (count < 0 || count > source.Length - _position)
                throw new FormatException("A VTClassic length-delimited block is truncated.");
            string value = source.Substring(_position, count);
            _position += count;
            return value;
        }
    }
}
