using AcDream.App.Configuration;
using AcDream.App.Credentials;

namespace AcDream.App.Tests.Credentials;

public sealed class AppCredentialResolverTests
{
    [Fact]
    public void EnvironmentSecretIsRedactedAndErasable()
    {
        const string variable = "ACDREAM_LA1_TEST_ENV_SECRET";
        const string secretValue = "test-secret-value";
        Environment.SetEnvironmentVariable(variable, secretValue);
        try
        {
            var resolver = new AppCredentialResolver(
                TextReader.Null,
                Environment.CurrentDirectory,
                isLinux: false);

            AppCredentialSecret secret = resolver.Resolve(
                "session",
                new SessionCredentialDescriptor
                {
                    Provider = SessionCredentialProviderKind.Environment,
                    Reference = variable,
                });

            Assert.Equal(secretValue, secret.Reveal());
            Assert.DoesNotContain(secretValue, secret.ToString());
            secret.Dispose();
            Assert.True(secret.IsDisposed);
            Assert.Throws<ObjectDisposedException>(secret.Reveal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void StandardInputConsumesOneSecretWithoutEchoingIt()
    {
        const string secretValue = "stdin-secret";
        var resolver = new AppCredentialResolver(
            new StringReader(secretValue + Environment.NewLine),
            Environment.CurrentDirectory,
            isLinux: false);

        using AppCredentialSecret secret = resolver.Resolve(
            "session",
            new SessionCredentialDescriptor
            {
                Provider = SessionCredentialProviderKind.StandardInput,
                Reference = "session-stdin",
            });

        Assert.Equal(secretValue, secret.Reveal());
        Assert.DoesNotContain(secretValue, secret.ToString());
    }

    [Fact]
    public void CredentialFileIsResolvedRelativeToConfiguredDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"acdream-app-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "session.pass");
        File.WriteAllText(path, "file-secret" + Environment.NewLine);
        try
        {
            var resolver = new AppCredentialResolver(
                TextReader.Null,
                directory,
                isLinux: false);

            using AppCredentialSecret secret = resolver.Resolve(
                "session",
                new SessionCredentialDescriptor
                {
                    Provider = SessionCredentialProviderKind.File,
                    Reference = "session.pass",
                });

            Assert.Equal("file-secret", secret.Reveal());
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void MissingSecretErrorNeverContainsAnotherSecret()
    {
        const string variable = "ACDREAM_LA1_TEST_OTHER_SECRET";
        const string unrelatedSecret = "must-not-leak";
        Environment.SetEnvironmentVariable(variable, unrelatedSecret);
        try
        {
            var resolver = new AppCredentialResolver(
                new StringReader(string.Empty),
                Environment.CurrentDirectory,
                isLinux: false);

            AppCredentialException error =
                Assert.Throws<AppCredentialException>(() =>
                    resolver.Resolve(
                        "session",
                        new SessionCredentialDescriptor
                        {
                            Provider = SessionCredentialProviderKind.Environment,
                            Reference = "ACDREAM_LA1_TEST_DOES_NOT_EXIST",
                        }));

            Assert.DoesNotContain(unrelatedSecret, error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    [Trait("Lane", "Linux")]
    public void LinuxRejectsGroupOrOtherCredentialPermissions()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Lane=Linux requires a native Linux host.");

        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-app-credential-{Guid.NewGuid():N}");
        File.WriteAllText(path, "linux-secret");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.GroupRead);
        try
        {
            var resolver = new AppCredentialResolver(
                TextReader.Null,
                Path.GetDirectoryName(path)!,
                isLinux: true);

            Assert.Throws<AppCredentialException>(() =>
                resolver.Resolve(
                    "session",
                    new SessionCredentialDescriptor
                    {
                        Provider = SessionCredentialProviderKind.File,
                        Reference = Path.GetFileName(path),
                    }));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
