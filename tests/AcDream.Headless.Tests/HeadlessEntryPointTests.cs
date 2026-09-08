using AcDream.Headless.Configuration;

namespace AcDream.Headless.Tests;

public sealed class HeadlessEntryPointTests
{
    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void HelpIsPresentationFreeAndSuccessful(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            [argument],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("acdream-headless", output.ToString());
        Assert.Contains("--password", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void RunCommandAcceptsDirectCredentialAliases()
    {
        HeadlessCommandLine command = HeadlessCommandLine.Parse(
        [
            "run",
            "--config",
            "bot.json",
            "-user",
            "testaccount",
            "--password",
            "fixture-password",
        ]);

        Assert.Equal("testaccount", command.DirectCredentials?.User);
        Assert.Equal(
            "fixture-password",
            command.DirectCredentials?.Password);
    }

    [Theory]
    [InlineData("-user", "testaccount")]
    [InlineData("--password", "fixture-password")]
    public void DirectCredentialPairMustBeComplete(
        string option,
        string value)
    {
        Assert.Throws<HeadlessCommandLineException>(
            () => HeadlessCommandLine.Parse(
            [
                "run",
                "--config",
                "bot.json",
                option,
                value,
            ]));
    }

    [Fact]
    public void ValidateAcceptsStrictEmptyNoConnectConfiguration()
    {
        using var file = TemporaryConfiguration.Create(
            """{"version":1,"sessions":[]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("0 session(s)", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("""{"version":2,"sessions":[]}""", "Unsupported configuration version")]
    [InlineData("""{"version":1,"sessions":[],"password":"secret"}""", "JSON document is invalid")]
    [InlineData("""{"version":1,"sessions":[null]}""", "non-empty id")]
    [InlineData("""{"version":1}""", "JSON document is invalid")]
    public void ValidateRejectsInvalidOrSecretShapedConfiguration(
        string json,
        string expectedError)
    {
        using var file = TemporaryConfiguration.Create(json);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(3, exitCode);
        Assert.Contains(
            expectedError,
            error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void ValidateAcceptsCompleteSingleSessionConfiguration()
    {
        using var file = TemporaryConfiguration.Create(
            ConfigurationWith(Session("bot", "BOT_PASSWORD")));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("1 session(s)", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void ValidateAcceptsCompleteProcessContentDescriptor()
    {
        using var file = TemporaryConfiguration.Create(
            """{"version":1,"process":{"content":{"datDirectory":"C:\\AC","preparedAssetPath":"C:\\AC\\acdream.pak"}},"sessions":[]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Contains("0 session(s)", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Theory]
    [InlineData("""{"version":1,"process":{"content":{"datDirectory":"","preparedAssetPath":"package.pak"}},"sessions":[]}""")]
    [InlineData("""{"version":1,"process":{"content":{"datDirectory":"dats","preparedAssetPath":""}},"sessions":[]}""")]
    public void ValidateRejectsIncompleteProcessContentDescriptor(string json)
    {
        using var file = TemporaryConfiguration.Create(json);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(3, exitCode);
        Assert.Contains(
            "process.content",
            error.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-credential")]
    public void ValidateRejectsAmbiguousSessionOwnership(string scenario)
    {
        string second = scenario == "duplicate-id"
            ? Session("bot", "SECOND_PASSWORD")
            : Session("second", "BOT_PASSWORD");
        string expected = scenario == "duplicate-id"
            ? "Duplicate session id"
            : "already in use";
        using var file = TemporaryConfiguration.Create(
            ConfigurationWith(
                Session("bot", "BOT_PASSWORD"),
                second));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal(3, exitCode);
        Assert.Contains(expected, error.ToString());
    }

    [Theory]
    [InlineData(
        """
        {"version":1,"sessions":[{"id":"s","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","policy":{"id":"idle"},"credential":{"provider":"environment","reference":"X"}}]}
        """,
        "character selector")]
    [InlineData(
        """
        {"version":1,"sessions":[{"id":"s","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","character":{"index":0},"credential":{"provider":"environment","reference":"X"}}]}
        """,
        "policy id")]
    public void PlaySessionMissingCharacterOrPolicyKeepsConfigurationErrorExitCode(
        string json,
        string expectedMessageFragment)
    {
        using var file = TemporaryConfiguration.Create(json);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal((int)HeadlessExitCode.ConfigurationError, exitCode);
        Assert.Contains(
            expectedMessageFragment,
            error.ToString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, output.ToString());
    }

    [Fact]
    public void ValidateAcceptsProbeSessionOmittingCharacterAndPolicy()
    {
        using var file = TemporaryConfiguration.Create(
            """
            {"version":1,"sessions":[{"id":"probe","endpoint":{"host":"127.0.0.1","port":9000},"account":"account","mode":"probe","credential":{"provider":"environment","reference":"X"}}]}
            """);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["validate", "--config", file.Path],
            output,
            error);

        Assert.Equal((int)HeadlessExitCode.Success, exitCode);
        Assert.Contains("1 session(s)", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void UnknownCommandReturnsUsageError()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = HeadlessEntryPoint.Run(
            ["connect", "--password", "secret"],
            output,
            error);

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Equal(string.Empty, output.ToString());
    }

    private static string ConfigurationWith(params string[] sessions) =>
        $$"""{"version":1,"sessions":[{{string.Join(",", sessions)}}]}""";

    private static string Session(string id, string credentialReference) =>
        $"{{\"id\":\"{id}\",\"endpoint\":{{\"host\":\"127.0.0.1\",\"port\":9000}},"
        + "\"account\":\"account\",\"character\":{\"index\":0},"
        + "\"policy\":{\"id\":\"idle\"},\"credential\":"
        + $"{{\"provider\":\"environment\",\"reference\":\"{credentialReference}\"}}}}";

    private sealed class TemporaryConfiguration : IDisposable
    {
        private TemporaryConfiguration(string path)
        {
            Path = path;
        }

        internal string Path { get; }

        internal static TemporaryConfiguration Create(string json)
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"acdream-headless-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TemporaryConfiguration(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
