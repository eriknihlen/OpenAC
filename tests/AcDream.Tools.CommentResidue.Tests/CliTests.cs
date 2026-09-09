using AcDream.Tools.CommentResidue;

namespace AcDream.Tools.CommentResidue.Tests;

public sealed class CliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "comment-residue-" + Guid.NewGuid().ToString("N"));

    public CliTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void NewMatchingLine_FailsAndPrintsPathAndLine()
    {
        string patterns = Write("patterns.txt", "\\bAD-\\d+\\b\n");
        string baseline = Write("baseline.txt", "");
        Write("a.cs", "int a = 1;\n// see register AD-12 for the note\n");

        var output = new StringWriter();
        var original = Console.Out;
        Console.SetOut(output);
        int exit;
        try
        {
            exit = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline]);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(1, exit);
        Assert.Contains("a.cs:2: // see register AD-12 for the note", output.ToString());
    }

    [Fact]
    public void MatchingLineInBaseline_Passes()
    {
        string patterns = Write("patterns.txt", "\\bAD-\\d+\\b\n");
        string baseline = Write("baseline.txt", "a.cs|// see register AD-12 for the note\n");
        Write("a.cs", "int a = 1;\n// see register AD-12 for the note\n");

        int exit = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void UpdateBaseline_WritesSortedEntries()
    {
        string patterns = Write("patterns.txt", "\\bAD-\\d+\\b\n");
        string baseline = Write("baseline.txt", "");
        Write("z.cs", "// AD-99\n");
        Write("a.cs", "// AD-12\n");

        int exit = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline, "--update-baseline"]);

        Assert.Equal(0, exit);
        string[] lines = File.ReadAllLines(baseline);
        Assert.Equal(2, lines.Length);
        Assert.Equal("a.cs|// AD-12", lines[0]);
        Assert.Equal("z.cs|// AD-99", lines[1]);

        // The freshly written baseline must itself pass a normal run.
        int recheck = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline]);
        Assert.Equal(0, recheck);
    }

    [Fact]
    public void StaleBaselineEntry_WarnsOnStderrButPasses()
    {
        string patterns = Write("patterns.txt", "\\bAD-\\d+\\b\n");
        // This baseline entry matches no file on disk (the line it once
        // pinned has since been removed or reworded).
        string baseline = Write("baseline.txt", "gone.cs|// AD-1 stale entry\n");
        Write("a.cs", "int a = 1;\n");

        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        int exit;
        try
        {
            exit = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline]);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(0, exit);
        Assert.Contains("stale", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gone.cs|// AD-1 stale entry", error.ToString());
    }

    [Fact]
    public void FileUnderBinDirectory_IsIgnored()
    {
        string patterns = Write("patterns.txt", "\\bAD-\\d+\\b\n");
        string baseline = Write("baseline.txt", "");
        Write("bin/ignored.cs", "// AD-12\n");

        int exit = Cli.Run(["--root", _root, "--patterns", patterns, "--baseline", baseline]);

        Assert.Equal(0, exit);
    }

    [Fact]
    public void MissingArgument_ReturnsUsageErrorExitCode()
    {
        int exit = Cli.Run(["--root", _root]);

        Assert.Equal(2, exit);
    }

    private string Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
