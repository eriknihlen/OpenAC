using AcDream.App.Configuration;
using AcDream.App.Platform;

namespace AcDream.App.Credentials;

internal sealed class AppCredentialResolver
{
    private const UnixFileMode NonUserPermissionMask =
        UnixFileMode.GroupRead
        | UnixFileMode.GroupWrite
        | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead
        | UnixFileMode.OtherWrite
        | UnixFileMode.OtherExecute;

    private readonly TextReader _standardInput;
    private readonly string _credentialBaseDirectory;
    private readonly bool _isLinux;

    internal AppCredentialResolver(
        TextReader standardInput,
        string credentialBaseDirectory,
        bool isLinux)
    {
        _standardInput = standardInput
            ?? throw new ArgumentNullException(nameof(standardInput));
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialBaseDirectory);
        _credentialBaseDirectory = Path.GetFullPath(credentialBaseDirectory);
        _isLinux = isLinux;
    }

    internal AppCredentialSecret Resolve(
        string sessionId,
        SessionCredentialDescriptor credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(credential);

        string value;
        try
        {
            value = credential.Provider switch
            {
                SessionCredentialProviderKind.Environment =>
                    ResolveEnvironment(credential.Reference),
                SessionCredentialProviderKind.StandardInput =>
                    ResolveStandardInput(credential.Reference),
                SessionCredentialProviderKind.File =>
                    ResolveFile(credential.Reference),
                _ => throw new AppCredentialException(
                    $"Session '{sessionId}' uses an unsupported credential provider."),
            };
        }
        catch (AppCredentialException)
        {
            throw;
        }
        catch (Exception error)
            when (error is IOException
                or UnauthorizedAccessException
                or ArgumentException
                or NotSupportedException)
        {
            throw new AppCredentialException(
                $"Credential '{credential.Reference}' for session '{sessionId}' could not be resolved.",
                error);
        }

        try
        {
            return new AppCredentialSecret(credential.Reference, value.AsSpan());
        }
        finally
        {
            value = string.Empty;
        }
    }

    private static string ResolveEnvironment(string reference)
    {
        string? value = Environment.GetEnvironmentVariable(reference);
        if (string.IsNullOrEmpty(value))
        {
            throw new AppCredentialException(
                $"Credential environment reference '{reference}' is unavailable.");
        }
        return value;
    }

    private string ResolveStandardInput(string reference)
    {
        string? value = _standardInput.ReadLine();
        if (string.IsNullOrEmpty(value))
        {
            throw new AppCredentialException(
                $"Credential standard-input reference '{reference}' is unavailable.");
        }
        return value;
    }

    private string ResolveFile(string reference)
    {
        string path = Path.GetFullPath(reference, _credentialBaseDirectory);
        var file = new FileInfo(path);
        if (file.LinkTarget is not null)
        {
            throw new AppCredentialException(
                $"Credential file reference '{reference}' cannot be a symbolic link.");
        }

        if (RuntimePlatformGuard.IsLinuxRuntime && _isLinux)
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & NonUserPermissionMask) != 0
                || (mode & UnixFileMode.UserRead) == 0)
            {
                throw new AppCredentialException(
                    $"Credential file reference '{reference}' must be readable only by its owner.");
            }
        }

        string value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (value.Length == 0)
        {
            throw new AppCredentialException(
                $"Credential file reference '{reference}' is empty.");
        }
        return value;
    }
}
