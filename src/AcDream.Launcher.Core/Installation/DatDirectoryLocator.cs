namespace AcDream.Launcher.Core.Installation;

public sealed class DatDirectoryLocator
{
    public static IReadOnlyList<string> RequiredFileNames { get; } =
        Array.AsReadOnly<string>(
        [
            "client_portal.dat",
            "client_cell_1.dat",
            "client_highres.dat",
            "client_local_English.dat",
        ]);

    private readonly bool _isWindows;
    private readonly string[] _windowsCandidates;
    private readonly Func<string, bool> _directoryExists;
    private readonly Func<string, bool> _fileExists;

    public DatDirectoryLocator(
        bool? isWindows = null,
        IEnumerable<string>? windowsCandidates = null,
        Func<string, bool>? directoryExists = null,
        Func<string, bool>? fileExists = null)
    {
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
        _windowsCandidates = (windowsCandidates ?? DefaultWindowsCandidates())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _directoryExists = directoryExists ?? Directory.Exists;
        _fileExists = fileExists ?? File.Exists;
    }

    public IReadOnlyList<DatDirectoryValidation> Detect()
    {
        if (!_isWindows)
        {
            return [];
        }

        return _windowsCandidates
            .Where(_directoryExists)
            .Select(Validate)
            .ToArray();
    }

    public DatDirectoryValidation Validate(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return DatDirectoryValidation.Invalid(
                directory ?? string.Empty,
                "Choose the folder containing the retail DAT files.",
                RequiredFileNames);
        }

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException)
        {
            return DatDirectoryValidation.Invalid(
                directory,
                "The DAT directory path is not valid.",
                RequiredFileNames);
        }

        if (!_directoryExists(fullPath))
        {
            return DatDirectoryValidation.Invalid(
                fullPath,
                "The DAT directory does not exist.",
                RequiredFileNames);
        }

        string[] missing = RequiredFileNames
            .Where(fileName => !_fileExists(Path.Combine(fullPath, fileName)))
            .ToArray();
        return missing.Length == 0
            ? DatDirectoryValidation.Valid(fullPath)
            : DatDirectoryValidation.Invalid(
                fullPath,
                "The selected directory is missing required retail DAT files.",
                missing);
    }

    private static IEnumerable<string> DefaultWindowsCandidates()
    {
        string userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(
                userProfile,
                "Documents",
                "Asheron's Call");
        }

        yield return @"C:\Turbine\Asheron's Call";
    }
}

public sealed record DatDirectoryValidation(
    string Directory,
    bool IsValid,
    string Message,
    IReadOnlyList<string> MissingFileNames)
{
    internal static DatDirectoryValidation Valid(string directory) =>
        new(
            directory,
            true,
            "All four required retail DAT files were found.",
            []);

    internal static DatDirectoryValidation Invalid(
        string directory,
        string message,
        IReadOnlyList<string> missingFileNames) =>
        new(directory, false, message, missingFileNames);
}
