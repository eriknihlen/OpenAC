using System.Text.Json;
using AcDream.Headless.Configuration;

namespace AcDream.Headless.Tests;

public sealed class HeadlessConfigurationLoaderTests
{
    [Fact]
    public void ValidCharacterOptionsBlockParsesExactDeclaredNames()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                """
                "characterOptions":{
                    "IgnoreAllegianceRequests":true,
                    "ListenToTradeChat":false,
                    "AutoTarget":true
                }
                """)));

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        Dictionary<string, bool>? declared =
            Assert.Single(configuration.Sessions)!.CharacterOptions;
        Assert.NotNull(declared);
        Assert.Equal(3, declared!.Count);
        Assert.True(declared["IgnoreAllegianceRequests"]);
        Assert.False(declared["ListenToTradeChat"]);
        Assert.True(declared["AutoTarget"]);
    }

    [Fact]
    public void UnknownOptionNameFailsLoadNamingTheOffendingKey()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"NotARealOption\":true}")));

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "NotARealOption",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationOnlyTierThreeOptionNameFailsLoad()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"ShowHelm\":true}")));

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "ShowHelm",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NumericKeyFailsLoadInsteadOfAliasingIntoAnAllowedId()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"15\":true}")));

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains("15", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommaCombinedKeyFailsLoadInsteadOfOrCombiningIntoAnAllowedId()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"ToggleRun,AutoTarget\":true}")));

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "ToggleRun,AutoTarget",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BothFellowshipExclusionOptionsTrueFailsLoadNamingBothKeys()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"IgnoreFellowshipRequests\":true,"
                + "\"FellowshipAutoAcceptRequests\":true}")));

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "IgnoreFellowshipRequests",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "FellowshipAutoAcceptRequests",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonBoolValueFailsLoad()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{\"IgnoreAllegianceRequests\":\"yes\"}")));

        Assert.ThrowsAny<Exception>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Fact]
    public void AbsentCharacterOptionsBlockIsANoOp()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session("bot", "BOT_PASSWORD")));

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        Assert.Null(Assert.Single(configuration.Sessions)!.CharacterOptions);
    }

    [Fact]
    public void EmptyCharacterOptionsBlockIsANoOp()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "BOT_PASSWORD",
                "\"characterOptions\":{}")));

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        Dictionary<string, bool>? declared =
            Assert.Single(configuration.Sessions)!.CharacterOptions;
        Assert.NotNull(declared);
        Assert.Empty(declared!);
    }


    [Fact]
    public void ProbeSessionOmittingCharacterAndPolicyLoads()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "probe-session",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "mode": "probe",
                  "credential": { "provider": "environment", "reference": "PROBE_PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfiguration configuration =
            HeadlessConfigurationLoader.Load(file.Path);

        HeadlessSessionDescriptor session = Assert.Single(configuration.Sessions)!;
        Assert.Null(session.Character);
        Assert.Null(session.Policy);
    }

    [Theory]
    [InlineData("\"play\"")]
    [InlineData("0")]
    public void SessionModeRejectsEveryValueOtherThanTheNamedProbeMode(
        string modeJson)
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            $$"""
            {
              "version": 1,
              "sessions": [
                {
                  "id": "unsupported-mode",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "mode": {{modeJson}},
                  "credential": { "provider": "environment", "reference": "PROBE_PASSWORD" }
                }
              ]
            }
            """);

        Assert.Throws<JsonException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Theory]
    [InlineData(false, "mode")]
    [InlineData(false, "character")]
    [InlineData(false, "policy")]
    [InlineData(true, "character")]
    [InlineData(true, "policy")]
    public void ConditionalSessionFieldsRejectExplicitJsonNull(
        bool probe,
        string nullField)
    {
        string mode = probe
            ? "\"mode\":\"probe\","
            : nullField == "mode"
                ? "\"mode\":null,"
                : string.Empty;
        string character = nullField == "character"
            ? "\"character\":null,"
            : probe
                ? string.Empty
                : "\"character\":{\"index\":0},";
        string policy = nullField == "policy"
            ? "\"policy\":null,"
            : probe
                ? string.Empty
                : "\"policy\":{\"id\":\"idle\"},";
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            $$"""
            {
              "version": 1,
              "sessions": [
                {
                  "id": "explicit-null",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  {{mode}}
                  {{character}}
                  {{policy}}
                  "credential": { "provider": "environment", "reference": "PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            $"'{nullField}'",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "cannot be null",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PresenceAwareValidationKeepsUnmappedMemberRejection()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            ConfigurationWith(Session(
                "bot",
                "PASSWORD",
                "\"notAContractField\":true")));

        Assert.Throws<JsonException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Fact]
    public void PresenceAwareValidationKeepsTypedValueRejection()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "wrong-type",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "mode": { "value": "probe" },
                  "credential": { "provider": "environment", "reference": "PASSWORD" }
                }
              ]
            }
            """);

        Assert.Throws<JsonException>(
            () => HeadlessConfigurationLoader.Load(file.Path));
    }

    [Fact]
    public void ProbeSessionDeclaringCharacterFailsLoadNamingTheField()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "probe-with-character",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "mode": "probe",
                  "character": { "index": 0 },
                  "credential": { "provider": "environment", "reference": "PROBE_PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains("probe", exception.Message, StringComparison.Ordinal);
        Assert.Contains("character", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeSessionDeclaringPolicyFailsLoadNamingTheField()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "probe-with-policy",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "mode": "probe",
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "PROBE_PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains("probe", exception.Message, StringComparison.Ordinal);
        Assert.Contains("policy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaySessionMissingCharacterStillFailsLoad()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "play-missing-character",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "PLAY_PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "requires a character selector",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaySessionMissingPolicyStillFailsLoad()
    {
        using TemporaryConfiguration file = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "play-missing-policy",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "credential": { "provider": "environment", "reference": "PLAY_PASSWORD" }
                }
              ]
            }
            """);

        HeadlessConfigurationException exception = Assert.Throws<
            HeadlessConfigurationException>(
                () => HeadlessConfigurationLoader.Load(file.Path));

        Assert.Contains(
            "requires a non-empty policy id",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlaySessionKeepsTodaysStrictCharacterSelectorAndPolicyValidation()
    {
        using TemporaryConfiguration badSelector = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "bad-selector",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0, "name": "Two" },
                  "policy": { "id": "idle" },
                  "credential": { "provider": "environment", "reference": "A" }
                }
              ]
            }
            """);
        using TemporaryConfiguration blankPolicy = TemporaryConfiguration.Create(
            """
            {
              "version": 1,
              "sessions": [
                {
                  "id": "blank-policy",
                  "endpoint": { "host": "127.0.0.1", "port": 9000 },
                  "account": "account",
                  "character": { "index": 0 },
                  "policy": { "id": "" },
                  "credential": { "provider": "environment", "reference": "B" }
                }
              ]
            }
            """);

        Assert.Throws<HeadlessConfigurationException>(
            () => HeadlessConfigurationLoader.Load(badSelector.Path));
        Assert.Throws<HeadlessConfigurationException>(
            () => HeadlessConfigurationLoader.Load(blankPolicy.Path));
    }

    private static string ConfigurationWith(params string[] sessions) =>
        $$"""{"version":1,"sessions":[{{string.Join(",", sessions)}}]}""";

    private static string Session(
        string id,
        string credentialReference,
        string? extraTopLevelField = null)
    {
        string suffix = extraTopLevelField is null
            ? string.Empty
            : $",{extraTopLevelField}";
        return $"{{\"id\":\"{id}\",\"endpoint\":{{\"host\":\"127.0.0.1\",\"port\":9000}},"
            + "\"account\":\"account\",\"character\":{\"index\":0},"
            + "\"policy\":{\"id\":\"idle\"},\"credential\":"
            + $"{{\"provider\":\"environment\",\"reference\":\"{credentialReference}\"}}"
            + suffix
            + "}";
    }

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
                $"acdream-headless-op7-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return new TemporaryConfiguration(path);
        }

        public void Dispose()
        {
            File.Delete(Path);
        }
    }
}
