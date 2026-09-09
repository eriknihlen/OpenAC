using System.Text;
using System.Text.RegularExpressions;

namespace AcDream.Tools.CommentResidue;

public static class Program
{
    public static int Main(string[] args) => Cli.Run(args);
}

public sealed class ResiduePatterns
{
    private readonly Regex[] _patterns;

    public ResiduePatterns(IEnumerable<string> lines)
    {
        _patterns = lines
            .Select(static p => p.Trim())
            .Where(static p => p.Length > 0 && !p.StartsWith('#'))
            .Select(static p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
            .ToArray();
        if (_patterns.Length == 0)
            throw new ArgumentException("At least one pattern is required.", nameof(lines));
    }

    public static ResiduePatterns Load(string path) => new(File.ReadAllLines(path));

    public bool IsSuspicious(string text) => _patterns.Any(p => p.IsMatch(text));
}

/// <summary>One matching line found while scanning the tree.</summary>
public readonly record struct Hit(string RelativePath, int Line, string Text);

public static class Cli
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".git", "node_modules"];

    private static readonly HashSet<string> ResidueExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".ps1", ".psm1", ".yml", ".yaml", ".glsl", ".vert", ".frag", ".comp", ".geom",
        ".xml", ".axaml", ".csproj", ".props", ".targets", ".slnx", ".md", ".json", ".txt",
    };

    public static int Run(string[] args)
    {
        try
        {
            string? root = Option(args, "--root");
            string? patternsPath = Option(args, "--patterns");
            string? baselinePath = Option(args, "--baseline");
            bool updateBaseline = args.Contains("--update-baseline");

            if (root is null || patternsPath is null || baselinePath is null)
            {
                Console.Error.WriteLine("usage: CommentResidue --root <dir> --patterns <file> --baseline <file> [--update-baseline]");
                return 2;
            }

            root = Path.GetFullPath(root);
            ResiduePatterns patterns = ResiduePatterns.Load(patternsPath);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.GetFullPath(patternsPath),
                Path.GetFullPath(baselinePath),
            };
            List<Hit> hits = Scan(root, patterns, excluded);

            if (updateBaseline)
            {
                string[] written = WriteBaseline(baselinePath, hits);
                Console.WriteLine($"baseline updated: {written.Length} entries at {baselinePath}");
                return 0;
            }

            HashSet<string> baseline = ReadBaseline(baselinePath);
            var currentKeys = new HashSet<string>(hits.Select(BaselineKey), StringComparer.Ordinal);

            foreach (string entry in baseline.Where(e => !currentKeys.Contains(e)).OrderBy(e => e, StringComparer.Ordinal))
                Console.Error.WriteLine($"stale baseline entry (no longer matches anything): {entry}");

            List<Hit> newHits = hits.Where(h => !baseline.Contains(BaselineKey(h))).ToList();
            foreach (Hit hit in newHits)
                Console.WriteLine($"{hit.RelativePath}:{hit.Line}: {hit.Text}");

            if (newHits.Count > 0)
            {
                Console.Error.WriteLine($"comment residue: {newHits.Count} new line(s) not in the baseline");
                return 1;
            }

            Console.WriteLine($"comment residue: 0 new hits ({hits.Count} total, {baseline.Count} baselined)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static string BaselineKey(Hit hit) => $"{hit.RelativePath}|{hit.Text}";

    private static List<Hit> Scan(string root, ResiduePatterns patterns, HashSet<string> excluded)
    {
        var hits = new List<Hit>();
        foreach (string path in EnumerateFiles(root))
        {
            if (!ResidueExtensions.Contains(Path.GetExtension(path))) continue;
            if (excluded.Contains(Path.GetFullPath(path))) continue;
            string relative = Relative(root, path);
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!patterns.IsSuspicious(line)) continue;
                hits.Add(new Hit(relative, i + 1, line.Trim()));
            }
        }
        return hits;
    }

    private static HashSet<string> ReadBaseline(string path)
    {
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
        return new HashSet<string>(
            File.ReadAllLines(path).Where(static l => l.Length > 0),
            StringComparer.Ordinal);
    }

    private static string[] WriteBaseline(string path, List<Hit> hits)
    {
        string[] entries = hits
            .Select(BaselineKey)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static e => e, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllLines(path, entries);
        return entries;
    }

    private static readonly StringComparer SkippedDirectoryComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(directory))
            {
                if (SkippedDirectories.Contains(Path.GetFileName(child), SkippedDirectoryComparer)) continue;
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) continue;
                pending.Push(child);
            }
            foreach (string file in Directory.EnumerateFiles(directory))
                yield return file;
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string? Option(string[] args, string name)
    {
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
