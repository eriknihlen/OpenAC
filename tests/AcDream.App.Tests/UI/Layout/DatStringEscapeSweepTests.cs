using System.IO;
using System.Text;
using AcDream.App.UI.Layout;
using AcDream.Content;
using DatReaderWriter.Options;
using StringTable = DatReaderWriter.DBObjs.StringTable;

namespace AcDream.App.Tests.UI.Layout;

[Trait("Lane", "InstalledDat")]
public sealed class DatStringEscapeSweepTests
{
    [InstalledDatFact]
    public void EveryInstalledStringResolvesSourceDecoded()
    {
        string datDirectory = Environment.GetEnvironmentVariable("ACDREAM_DAT_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Documents",
                "Asheron's Call");
        using var dats = new DatCollection(datDirectory, DatAccessType.Read);
        var resolver = new DatStringResolver(dats);

        int tables = 0;
        int strings = 0;
        int withNewlineEscape = 0;
        int withTabEscape = 0;
        int withCrEscape = 0;
        int withQuoteEscape = 0;
        int withMetaSelfEscape = 0;
        int withUnknownPair = 0;
        int withRealCr = 0;
        var perTableNewlines = new SortedDictionary<uint, int>();
        var examples = new List<string>();
        string? userReportedExitText = null;

        foreach (uint tableId in dats.GetAllIdsOfType<StringTable>().Order())
        {
            StringTable? table = dats.Get<StringTable>(tableId);
            if (table is null)
                continue;
            tables++;

            foreach ((uint stringId, var entry) in table.Strings)
            {
                for (int token = 0; token < entry.Strings.Count; token++)
                {
                    string raw = entry.Strings[token].Value;
                    strings++;

                    bool newline = false, unknown = false, meta = false;
                    for (int i = 0; i < raw.Length - 1; i++)
                    {
                        if (raw[i] != '\\')
                            continue;
                        char next = raw[i + 1];
                        char decoded = RetailStringEscapes.GetUnEscapedChar(next);
                        switch (decoded)
                        {
                            case '\n': newline = true; break;
                            case '\t': withTabEscape++; break;
                            case '\r': withCrEscape++; break;
                            case '"': withQuoteEscape++; break;
                            case '\0': unknown = true; break;
                            default: meta = true; break;
                        }
                        i++;
                    }
                    if (newline)
                    {
                        withNewlineEscape++;
                        perTableNewlines[tableId] =
                            perTableNewlines.GetValueOrDefault(tableId) + 1;
                        if (examples.Count < 12)
                            examples.Add(
                                $"0x{tableId:X8}/0x{stringId:X8}: \"{Truncate(raw)}\"");
                    }
                    if (meta) withMetaSelfEscape++;
                    if (unknown) withUnknownPair++;
                    if (raw.Contains('\r')) withRealCr++;

                    Assert.Equal(
                        RetailStringEscapes.Unescape(raw),
                        resolver.Resolve(tableId, stringId, token));

                    if (raw.Contains("exit your character", StringComparison.OrdinalIgnoreCase))
                        userReportedExitText =
                            $"0x{tableId:X8}/0x{stringId:X8}: \"{raw}\"";
                }
            }
        }

        var summary = new StringBuilder()
            .AppendLine("[escape-sweep] installed-DAT string-table inventory:")
            .AppendLine($"  tables={tables} strings={strings}")
            .AppendLine($"  strings with literal \\n escape: {withNewlineEscape}")
            .AppendLine($"  \\t pairs: {withTabEscape}; \\r pairs: {withCrEscape}; \\q pairs: {withQuoteEscape}")
            .AppendLine($"  strings with metalanguage self-escapes: {withMetaSelfEscape}")
            .AppendLine($"  strings with unrecognized backslash pairs (kept verbatim): {withUnknownPair}")
            .AppendLine($"  strings containing a REAL CR character: {withRealCr}")
            .AppendLine("  \\n-escape counts per table: "
                + string.Join(", ", perTableNewlines.Select(
                    static pair => $"0x{pair.Key:X8}={pair.Value}")))
            .AppendLine("  examples:");
        foreach (string example in examples)
            summary.AppendLine($"    {example}");
        summary.AppendLine(userReportedExitText is null
            ? "  user-reported exit-world text: NOT found by content scan"
            : $"  user-reported exit-world text: {userReportedExitText}");
        Console.WriteLine(summary.ToString());

        Assert.True(
            withNewlineEscape > 0,
            "expected at least one installed string carrying the literal \\n escape");
    }

    private static string Truncate(string value) =>
        (value.Length <= 90 ? value : value[..90] + "…")
            .Replace("\r", "<CR>").Replace("\n", "<LF>");
}
