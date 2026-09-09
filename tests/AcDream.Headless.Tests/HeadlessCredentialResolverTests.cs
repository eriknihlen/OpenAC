using AcDream.Headless.Configuration;
using AcDream.Headless.Credentials;

namespace AcDream.Headless.Tests;

public sealed class HeadlessCredentialResolverTests
{
    [Fact]
    public void EnvironmentSecretIsRedactedAndErasable()
    {
        const string secretValue = "test-secret-value";
        var environment = new FixtureCredentialEnvironment(
            isLinux: false,
            new Dictionary<string, string>
            {
                ["BOT_PASSWORD"] = secretValue,
            });
        var resolver = new HeadlessCredentialResolver(
            TextReader.Null,
            Environment.CurrentDirectory,
            environment);

        HeadlessCredentialSecret secret = resolver.Resolve(
            "bot",
            new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.Environment,
                Reference = "BOT_PASSWORD",
            });

        Assert.Equal(secretValue, secret.Reveal());
        Assert.DoesNotContain(secretValue, secret.ToString());
        secret.Dispose();
        Assert.True(secret.IsDisposed);
        Assert.Throws<ObjectDisposedException>(secret.Reveal);
    }

    [Fact]
    public void StandardInputConsumesOneSecretWithoutEchoingIt()
    {
        const string secretValue = "stdin-secret";
        var resolver = new HeadlessCredentialResolver(
            new StringReader(secretValue + Environment.NewLine),
            Environment.CurrentDirectory,
            new FixtureCredentialEnvironment(
                false,
                new Dictionary<string, string>()));

        using HeadlessCredentialSecret secret = resolver.Resolve(
            "bot",
            new HeadlessCredentialReference
            {
                Provider = HeadlessCredentialProviderKind.StandardInput,
                Reference = "bot-stdin",
            });

        Assert.Equal(secretValue, secret.Reveal());
        Assert.DoesNotContain(secretValue, secret.ToString());
    }

    [Fact]
    public void CredentialFileIsResolvedRelativeToConfiguredDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"acdream-credentials-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "bot.pass");
        File.WriteAllText(path, "file-secret" + Environment.NewLine);
        try
        {
            var resolver = new HeadlessCredentialResolver(
                TextReader.Null,
                directory,
                new FixtureCredentialEnvironment(
                    false,
                    new Dictionary<string, string>()));

            using HeadlessCredentialSecret secret = resolver.Resolve(
                "bot",
                new HeadlessCredentialReference
                {
                    Provider = HeadlessCredentialProviderKind.File,
                    Reference = "bot.pass",
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
        const string unrelatedSecret = "must-not-leak";
        var resolver = new HeadlessCredentialResolver(
            new StringReader(string.Empty),
            Environment.CurrentDirectory,
            new FixtureCredentialEnvironment(
                false,
                new Dictionary<string, string>
                {
                    ["OTHER"] = unrelatedSecret,
                }));

        HeadlessCredentialException error =
            Assert.Throws<HeadlessCredentialException>(() =>
                resolver.Resolve(
                    "bot",
                    new HeadlessCredentialReference
                    {
                        Provider =
                            HeadlessCredentialProviderKind.Environment,
                        Reference = "MISSING",
                    }));

        Assert.DoesNotContain(unrelatedSecret, error.ToString());
    }

    [Fact]
    [Trait("Lane", "Linux")]
    public void LinuxRejectsGroupOrOtherCredentialPermissions()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Lane=Linux requires a native Linux host.");

        string path = Path.Combine(
            Path.GetTempPath(),
            $"acdream-credential-{Guid.NewGuid():N}");
        File.WriteAllText(path, "linux-secret");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.GroupRead);
        try
        {
            var resolver = new HeadlessCredentialResolver(
                TextReader.Null,
                Path.GetDirectoryName(path)!,
                new FixtureCredentialEnvironment(
                    true,
                    new Dictionary<string, string>()));

            Assert.Throws<HeadlessCredentialException>(() =>
                resolver.Resolve(
                    "bot",
                    new HeadlessCredentialReference
                    {
                        Provider = HeadlessCredentialProviderKind.File,
                        Reference = Path.GetFileName(path),
                    }));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FixtureCredentialEnvironment(
        bool isLinux,
        IReadOnlyDictionary<string, string> values)
        : IHeadlessCredentialEnvironment
    {
        public bool IsLinux { get; } = isLinux;

        public string? GetEnvironmentVariable(string name) =>
            values.TryGetValue(name, out string? value)
                ? value
                : null;
    }
}
