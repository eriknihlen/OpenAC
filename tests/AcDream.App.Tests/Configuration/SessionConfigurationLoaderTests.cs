using AcDream.App.Configuration;

namespace AcDream.App.Tests.Configuration;

public sealed class SessionConfigurationLoaderTests
{
    [Fact]
    public void ProcessPathsAreAcceptedButIgnored()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "process": {
                "paths": {
                  "configDirectory": "/config",
                  "dataDirectory": "/data",
                  "cacheDirectory": "/cache"
                }
              },
              "sessions": [
                {
                  "id": "paths-tolerant",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "credential": { "provider": "environment", "reference": "X" }
                }
              ]
            }
            """);

        (SessionConfiguration configuration, SessionDescriptor session) =
            SessionConfigurationLoader.Load(file.Path);

        Assert.Equal("paths-tolerant", session.Id);
        Assert.Equal("/config", configuration.Process?.Paths?.ConfigDirectory);
        Assert.Equal("/data", configuration.Process?.Paths?.DataDirectory);
        Assert.Equal("/cache", configuration.Process?.Paths?.CacheDirectory);
    }

    [Fact]
    public void AbsentModeIsTreatedAsPlay()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "no-mode",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "credential": { "provider": "environment", "reference": "X" }
                }
              ]
            }
            """);

        (_, SessionDescriptor session) = SessionConfigurationLoader.Load(file.Path);

        Assert.Null(session.Mode);
    }

    [Fact]
    public void ProbeModeFailsLoadWithAnExplicitHeadlessOnlyMessage()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "probe-session",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "credential": { "provider": "environment", "reference": "X" },
                  "mode": "probe"
                }
              ]
            }
            """);

        SessionConfigurationException error = Assert.Throws<SessionConfigurationException>(
            () => SessionConfigurationLoader.Load(file.Path));
        Assert.Contains("mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("probe", error.Message, StringComparison.Ordinal);
        Assert.Contains("headless-only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnrecognizedModeFailsLoad()
    {
        using TemporaryFile file = TemporaryFile.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "bad-mode",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "credential": { "provider": "environment", "reference": "X" },
                  "mode": "bogus"
                }
              ]
            }
            """);

        Assert.Throws<SessionConfigurationException>(
            () => SessionConfigurationLoader.Load(file.Path));
    }

    private sealed class TemporaryFile : IDisposable
    {
        private TemporaryFile(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryFile Create(string json)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-app-la1-loader-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TemporaryFile(path);
        }

        public void Dispose() => File.Delete(Path);
    }
}
