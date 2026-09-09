namespace AcDream.Headless.Configuration;

internal static class HeadlessConsoleOptions
{
    internal const string EnvironmentVariable = "ACDREAM_HEADLESS_CONSOLE";

    internal static bool Resolve(
        bool commandLineFlag,
        bool standardInputIsTerminal) =>
        Resolve(
            commandLineFlag,
            Environment.GetEnvironmentVariable,
            standardInputIsTerminal);

    internal static bool Resolve(
        bool commandLineFlag,
        Func<string, string?> env,
        bool standardInputIsTerminal)
    {
        ArgumentNullException.ThrowIfNull(env);
        if (commandLineFlag)
            return true;
        if (env(EnvironmentVariable) is null)
            return standardInputIsTerminal;
        return !string.Equals(
            env("ACDREAM_HEADLESS_CONSOLE"), "0", StringComparison.Ordinal);
    }
}
