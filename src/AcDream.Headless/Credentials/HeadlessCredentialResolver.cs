using AcDream.Headless.Configuration;

namespace AcDream.Headless.Credentials;

internal interface IHeadlessCredentialEnvironment
{
    bool IsLinux { get; }
    string? GetEnvironmentVariable(string name);
}

internal sealed class HeadlessCredentialEnvironment
    : IHeadlessCredentialEnvironment
{
    internal static HeadlessCredentialEnvironment Instance { get; } = new();

    private HeadlessCredentialEnvironment()
    {
    }

    public bool IsLinux => OperatingSystem.IsLinux();

    public string? GetEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name);
}

internal sealed class HeadlessCredentialResolver
{
    private const UnixFileMode NonUserPermissionMask =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private readonly TextReader _standardInput;
    private readonly IHeadlessCredentialEnvironment _environment;
    private readonly string _credentialBaseDirectory;

    internal HeadlessCredentialResolver(
        TextReader standardInput,
        string credentialBaseDirectory,
        IHeadlessCredentialEnvironment? environment = null)
    {
        _standardInput = standardInput
            ?? throw new ArgumentNullException(nameof(standardInput));
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialBaseDirectory);
        _credentialBaseDirectory = Path.GetFullPath(
            credentialBaseDirectory);
        _environment =
            environment ?? HeadlessCredentialEnvironment.Instance;
    }

    internal HeadlessCredentialSecret Resolve(
        string sessionId,
        HeadlessCredentialReference credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(credential);

        string value;
        try
        {
            value = credential.Provider switch
            {
                HeadlessCredentialProviderKind.Environment =>
                    ResolveEnvironment(credential.Reference),
                HeadlessCredentialProviderKind.StandardInput =>
                    ResolveStandardInput(credential.Reference),
                HeadlessCredentialProviderKind.File =>
                    ResolveFile(credential.Reference),
                _ => throw new HeadlessCredentialException(
                    $"Session '{sessionId}' uses an unsupported credential provider."),
            };
        }
        catch (HeadlessCredentialException)
        {
            throw;
        }
        catch (Exception error)
            when (error is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            throw new HeadlessCredentialException(
                $"Credential '{credential.Reference}' for session '{sessionId}' could not be resolved.",
                error);
        }

        try
        {
            return new HeadlessCredentialSecret(
                credential.Reference,
                value.AsSpan());
        }
        finally
        {
            value = string.Empty;
        }
    }

    private string ResolveEnvironment(string reference)
    {
        string? value = _environment.GetEnvironmentVariable(reference);
        if (string.IsNullOrEmpty(value))
        {
            throw new HeadlessCredentialException(
                $"Credential environment reference '{reference}' is unavailable.");
        }
        return value;
    }

    private string ResolveStandardInput(string reference)
    {
        string? value = _standardInput.ReadLine();
        if (string.IsNullOrEmpty(value))
        {
            throw new HeadlessCredentialException(
                $"Credential standard-input reference '{reference}' is unavailable.");
        }
        return value;
    }

    private string ResolveFile(string reference)
    {
        string path = Path.GetFullPath(
            reference,
            _credentialBaseDirectory);
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            throw new HeadlessCredentialException(
                $"Credential file reference '{reference}' cannot be a symbolic link.");
        }

        if (OperatingSystem.IsLinux() && _environment.IsLinux)
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & NonUserPermissionMask) != 0
                || (mode & UnixFileMode.UserRead) == 0)
            {
                throw new HeadlessCredentialException(
                    $"Credential file reference '{reference}' must be readable only by its owner.");
            }
        }

        string value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (value.Length == 0)
        {
            throw new HeadlessCredentialException(
                $"Credential file reference '{reference}' is empty.");
        }
        return value;
    }
}
