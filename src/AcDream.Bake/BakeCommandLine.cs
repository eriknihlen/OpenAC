using System.Globalization;

namespace AcDream.Bake;

internal sealed record BakeCommandLineOptions(
    string DatDirectory,
    string OutputPath,
    HashSet<uint>? IdFilter,
    HashSet<uint>? LandblockFilter,
    int Threads,
    bool ProgressJson);

internal static class BakeCommandLine
{
    internal const string Usage =
        "usage: acdream-bake --dat-dir <path> [--out <file>] "
        + "[--ids 0xId,0xId,...] [--landblocks 0xId,...] "
        + "[--threads <n>] [--progress-json]\n"
        + "       acdream-bake --help";

    public static bool IsHelpRequest(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count == 1
            && args[0] is "--help" or "-h";
    }

    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter error,
        out BakeCommandLineOptions? options)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(error);

        string? datDirectory = null;
        string? outputPath = null;
        HashSet<uint>? idFilter = null;
        HashSet<uint>? landblockFilter = null;
        int threads = Environment.ProcessorCount;
        bool progressJson = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--dat-dir":
                    datDirectory = Value(args, ref i);
                    break;
                case "--out":
                    outputPath = Value(args, ref i);
                    break;
                case "--ids":
                    idFilter = ParseHexList(Value(args, ref i), error);
                    break;
                case "--landblocks":
                    landblockFilter = ParseHexList(Value(args, ref i), error);
                    break;
                case "--threads":
                    if (int.TryParse(
                            Value(args, ref i),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsedThreads)
                        && parsedThreads > 0)
                    {
                        threads = parsedThreads;
                    }
                    break;
                case "--progress-json":
                    progressJson = true;
                    break;
                default:
                    error.WriteLine($"unrecognized argument: {args[i]}");
                    options = null;
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(datDirectory))
        {
            error.WriteLine(Usage);
            options = null;
            return false;
        }

        outputPath ??= Path.Combine(datDirectory, "acdream.pak");
        options = new BakeCommandLineOptions(
            datDirectory,
            outputPath,
            idFilter,
            landblockFilter,
            threads,
            progressJson);
        return true;
    }

    private static string? Value(IReadOnlyList<string> args, ref int index) =>
        index + 1 < args.Count ? args[++index] : null;

    private static HashSet<uint> ParseHexList(string? raw, TextWriter error)
    {
        var result = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (string token in raw.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            string hex = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? token[2..]
                : token;
            if (uint.TryParse(
                    hex,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out uint value))
            {
                result.Add(value);
            }
            else
            {
                error.WriteLine($"warning: could not parse id '{token}' - skipped");
            }
        }

        return result;
    }
}
