namespace AcDream.Headless.Configuration;

internal sealed record HeadlessCommandLine(
    string Command,
    string ConfigurationPath,
    HeadlessPathOverrides Paths,
    HeadlessDirectCredentials? DirectCredentials,
    bool ConsoleEnabled = false)
{
    internal static HeadlessCommandLine Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count < 3)
            throw new HeadlessCommandLineException("Missing command options.");

        string command = arguments[0];
        if (command is not ("validate" or "run"))
            throw new HeadlessCommandLineException("Unknown command.");

        string? configurationPath = null;
        string? configDirectory = null;
        string? dataDirectory = null;
        string? cacheDirectory = null;
        string? user = null;
        string? password = null;
        bool console = false;
        int index = 1;
        while (index < arguments.Count)
        {
            string name = arguments[index];
            if (name == "--console")
            {
                console = true;
                index += 1;
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                throw new HeadlessCommandLineException(
                    "Every command option requires a value.");
            }

            string value = arguments[index + 1];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new HeadlessCommandLineException(
                    "Command option values cannot be empty.");
            }

            switch (name)
            {
                case "--config":
                    SetOnce(ref configurationPath, value);
                    break;
                case "--config-dir":
                    SetOnce(ref configDirectory, value);
                    break;
                case "--data-dir":
                    SetOnce(ref dataDirectory, value);
                    break;
                case "--cache-dir":
                    SetOnce(ref cacheDirectory, value);
                    break;
                case "-user":
                case "--user":
                    SetOnce(ref user, value);
                    break;
                case "-password":
                case "--password":
                    SetOnce(ref password, value);
                    break;
                default:
                    throw new HeadlessCommandLineException(
                        "Unknown command option.");
            }
            index += 2;
        }

        if (configurationPath is null)
        {
            throw new HeadlessCommandLineException(
                "The --config option is required.");
        }
        if ((user is null) != (password is null))
        {
            throw new HeadlessCommandLineException(
                "The user and password options must be supplied together.");
        }
        if (user is not null && command != "run")
        {
            throw new HeadlessCommandLineException(
                "Direct credentials are valid only for run mode.");
        }
        if (console && command != "run")
        {
            throw new HeadlessCommandLineException(
                "--console is valid only for run mode.");
        }

        return new HeadlessCommandLine(
            command,
            configurationPath,
            new HeadlessPathOverrides(
                configDirectory,
                dataDirectory,
                cacheDirectory),
            user is null
                ? null
                : new HeadlessDirectCredentials(user, password!),
            console);
    }

    private static void SetOnce(ref string? destination, string value)
    {
        if (destination is not null)
        {
            throw new HeadlessCommandLineException(
                "Command options cannot be repeated.");
        }
        destination = value;
    }
}

internal sealed record HeadlessDirectCredentials(
    string User,
    string Password);
