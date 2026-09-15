namespace AcDream.DrakBot.Remote.Tests;

public sealed class RemoteOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string> NoSettings = new Dictionary<string, string>();

    [Fact]
    public void NothingConfiguredLeavesTheRemoteOff()
    {
        RemoteOptions options = RemoteOptions.Resolve(new MemoryStorage(), NoSettings, _ => null);

        Assert.False(options.Enabled);
        Assert.Equal(RemoteOptions.DefaultPort, options.Port);
        Assert.False(options.BindsEveryInterface);
        Assert.Null(options.Token);
    }

    [Fact]
    public void TheStorageFileTurnsItOn()
    {
        var storage = new MemoryStorage();
        storage.WriteText(RemoteOptions.FileName, "{ \"enabled\": true, \"port\": 9001, \"bind\": \"any\", \"token\": \"s3cret\" }");

        RemoteOptions options = RemoteOptions.Resolve(storage, NoSettings, _ => null);

        Assert.True(options.Enabled);
        Assert.Equal(9001, options.Port);
        Assert.True(options.BindsEveryInterface);
        Assert.Equal("s3cret", options.Token);
        Assert.Null(options.Validate());
    }

    [Fact]
    public void SessionSettingsOverrideTheFileAndAPortAloneEnables()
    {
        var storage = new MemoryStorage();
        storage.WriteText(RemoteOptions.FileName, "{ \"enabled\": false, \"port\": 9001 }");
        var settings = new Dictionary<string, string> { ["port"] = "9100" };

        RemoteOptions options = RemoteOptions.Resolve(storage, settings, _ => null);

        Assert.True(options.Enabled);
        Assert.Equal(9100, options.Port);
    }

    [Fact]
    public void TheEnvironmentWinsOverEverything()
    {
        var storage = new MemoryStorage();
        storage.WriteText(RemoteOptions.FileName, "{ \"enabled\": true, \"port\": 9001, \"token\": \"file\" }");
        var settings = new Dictionary<string, string> { ["token"] = "session" };
        var environment = new Dictionary<string, string>
        {
            ["ACDREAM_REMOTE"] = "0",
            ["ACDREAM_REMOTE_TOKEN"] = "env",
            ["ACDREAM_REMOTE_BIND"] = "any",
        };

        RemoteOptions options = RemoteOptions.Resolve(storage, settings, key => environment.GetValueOrDefault(key));

        Assert.False(options.Enabled);
        Assert.Equal("env", options.Token);
        Assert.True(options.BindsEveryInterface);
        Assert.Equal(9001, options.Port);
    }

    [Fact]
    public void ListeningOnEveryInterfaceNeedsAToken()
    {
        var options = new RemoteOptions { Enabled = true, Bind = "any" };

        Assert.NotNull(options.Validate());
        Assert.Null((options with { Token = "x" }).Validate());
        Assert.NotNull((options with { Port = 0 }).Validate());
    }

    [Fact]
    public void ABrokenFileIsReportedAndIgnored()
    {
        var storage = new MemoryStorage();
        storage.WriteText(RemoteOptions.FileName, "{ not json");
        var warnings = new List<string>();

        RemoteOptions options = RemoteOptions.Resolve(storage, NoSettings, _ => null, warnings.Add);

        Assert.False(options.Enabled);
        Assert.Single(warnings);
    }

    [Fact]
    public void OptionsRoundTripThroughJson()
    {
        var options = new RemoteOptions { Enabled = true, Port = 8745, Bind = "any", Token = "t" };

        Assert.Equal(options, RemoteOptions.FromJson(options.ToJson()));
    }
}
