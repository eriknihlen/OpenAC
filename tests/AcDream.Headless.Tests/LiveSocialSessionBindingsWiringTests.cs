using System.Reflection;
using System.Runtime.CompilerServices;
using AcDream.Runtime.Session;

namespace AcDream.Headless.Tests;

/// <summary>
/// Guards against a repeat of the defect where HeadlessSessionHost's own
/// <c>LiveSocialSessionBindings</c> construction silently omitted
/// <c>Trade: Runtime.TradeOwner</c> -- every optional binding on the
/// record has a default of <c>null</c>, so a missing one is a silent
/// "this host doesn't care about X" rather than a compile error, and every
/// inbound wire message for that owner is then dropped with no signal.
/// </summary>
/// <remarks>
/// Reflects <see cref="LiveSocialSessionBindings"/>'s own constructor to
/// find every optional parameter (so a newly added owner is covered
/// automatically), then parses the top-level argument list at each host's
/// construction site -- honoring both positional and named-argument C#
/// call styles -- to check every optional parameter is actually supplied,
/// not silently defaulted. This is a static tripwire, not a live-value
/// check: it would not catch a supplied argument that is itself null at
/// runtime, only an argument omitted outright.
/// </remarks>
public sealed class LiveSocialSessionBindingsWiringTests
{
    [Fact]
    public void EveryOptionalSocialBindingIsWiredAtBothHostConstructionSites()
    {
        string repositoryRoot = FindRepositoryRoot();
        string appSource = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "AcDream.App", "Net", "LiveSessionRuntimeFactory.cs"));
        string headlessSource = File.ReadAllText(Path.Combine(
            repositoryRoot, "src", "AcDream.Headless", "Hosting", "HeadlessSessionHost.cs"));

        ParameterInfo[] parameters = typeof(LiveSocialSessionBindings)
            .GetConstructors()
            .Single()
            .GetParameters();
        string[] optionalNames = parameters
            .Where(static parameter => parameter.HasDefaultValue)
            .Select(static parameter => parameter.Name!)
            .ToArray();
        Assert.NotEmpty(optionalNames);

        foreach ((string hostName, string source) in new[]
        {
            ("AcDream.App", appSource),
            ("AcDream.Headless", headlessSource),
        })
        {
            string call = ExtractBalancedCall(source, "new LiveSocialSessionBindings(");
            HashSet<string> wired = SuppliedParameterNames(call, parameters);
            foreach (string optional in optionalNames)
            {
                Assert.True(
                    wired.Contains(optional),
                    $"{hostName}'s LiveSocialSessionBindings construction site "
                    + $"never supplies '{optional}' (neither positionally nor "
                    + "by name), so it silently defaults to null.");
            }
        }
    }

    /// <summary>
    /// Determines which declared parameters an argument list actually
    /// supplies, honoring C#'s rule that named arguments may appear in any
    /// order but positional arguments must all precede them and fill
    /// parameters left to right.
    /// </summary>
    private static HashSet<string> SuppliedParameterNames(
        string constructorCall, ParameterInfo[] parameters)
    {
        List<string> arguments = SplitTopLevelArguments(constructorCall);
        var wired = new HashSet<string>(StringComparer.Ordinal);
        int positionalIndex = 0;
        foreach (string rawArgument in arguments)
        {
            string argument = rawArgument.Trim();
            if (argument.Length == 0)
                continue;

            int colon = FindNamedArgumentColon(argument);
            if (colon >= 0)
            {
                wired.Add(argument[..colon].Trim());
                continue;
            }

            if (positionalIndex < parameters.Length)
                wired.Add(parameters[positionalIndex].Name!);
            positionalIndex++;
        }

        return wired;
    }

    /// <summary>
    /// Finds a top-level "name:" prefix, distinguishing it from the "::"
    /// or lambda "=>"/ternary "?:" sequences that can also contain a colon
    /// early in an argument (a bare identifier followed by exactly one
    /// colon, with no preceding operator character, is the C# named-
    /// argument syntax).
    /// </summary>
    private static int FindNamedArgumentColon(string argument)
    {
        for (int i = 0; i < argument.Length; i++)
        {
            char c = argument[i];
            if (char.IsLetterOrDigit(c) || c == '_')
                continue;
            if (c == ':' && i > 0 && argument[i - 1] != ':')
                return i;
            return -1;
        }
        return -1;
    }

    private static List<string> SplitTopLevelArguments(string call)
    {
        int openParenIndex = call.IndexOf('(');
        int closeParenIndex = call.LastIndexOf(')');
        string inner = call[(openParenIndex + 1)..closeParenIndex];

        var result = new List<string>();
        int depth = 0;
        int segmentStart = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c is '(' or '[' or '{')
                depth++;
            else if (c is ')' or ']' or '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                result.Add(inner[segmentStart..i]);
                segmentStart = i + 1;
            }
        }
        result.Add(inner[segmentStart..]);
        return result;
    }

    /// <summary>
    /// Returns the full <c>new Type(...)</c> call text starting at
    /// <paramref name="marker"/>, matching parentheses so a nested call
    /// (e.g. a lambda argument with its own parens) doesn't truncate it
    /// early.
    /// </summary>
    private static string ExtractBalancedCall(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find '{marker}' in the source file.");
        int openParenIndex = start + marker.Length - 1;
        int depth = 0;
        for (int i = openParenIndex; i < source.Length; i++)
        {
            if (source[i] == '(')
                depth++;
            else if (source[i] == ')')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException(
            $"Unbalanced parentheses starting at '{marker}'.");
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        {
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
                continue;

            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the working or output directory.");
    }
}
