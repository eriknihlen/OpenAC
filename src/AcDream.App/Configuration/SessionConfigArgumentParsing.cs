namespace AcDream.App.Configuration;

internal static class SessionConfigArgumentParsing
{
    internal static string? ExtractFlagValue(
        string[] arguments,
        string flag,
        out bool present)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);

        for (int i = 0; i < arguments.Length; i++)
        {
            if (!string.Equals(arguments[i], flag, StringComparison.Ordinal))
                continue;

            present = true;
            return i == arguments.Length - 1 ? null : arguments[i + 1];
        }

        present = false;
        return null;
    }

    internal static string[] WithoutFlagAndValue(string[] arguments, string flag)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(flag);

        var result = new List<string>(arguments.Length);
        for (int i = 0; i < arguments.Length; i++)
        {
            if (string.Equals(arguments[i], flag, StringComparison.Ordinal))
            {
                i++; // also skip the flag's value, if any
                continue;
            }
            result.Add(arguments[i]);
        }
        return [.. result];
    }
}
