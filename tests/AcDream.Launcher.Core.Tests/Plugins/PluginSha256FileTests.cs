using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginSha256FileTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ParsesHashOnlyContent()
    {
        PluginSha256File file = PluginSha256File.Parse(Hash + "\n");

        Assert.Equal(Hash, file.Sha256);
        Assert.Null(file.FileName);
    }

    [Fact]
    public void ParsesShasumStyleContentWithFileName()
    {
        PluginSha256File file = PluginSha256File.Parse($"{Hash}  edwards.hello-0.1.0.zip\n");

        Assert.Equal(Hash, file.Sha256);
        Assert.Equal("edwards.hello-0.1.0.zip", file.FileName);
    }

    [Fact]
    public void ParsesBinaryModeAsteriskPrefixedFileName()
    {
        PluginSha256File file = PluginSha256File.Parse($"{Hash} *edwards.hello-0.1.0.zip\n");

        Assert.Equal("edwards.hello-0.1.0.zip", file.FileName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("aaaa")]
    public void RejectsMalformedContent(string content)
    {
        Assert.Throws<LauncherUpdateException>(() => PluginSha256File.Parse(content));
    }

    [Fact]
    public void RequireMatchesAcceptsTheExpectedName()
    {
        PluginSha256File file = PluginSha256File.Parse($"{Hash}  edwards.hello-0.1.0.zip");

        file.RequireMatches("edwards.hello-0.1.0.zip");
    }

    [Fact]
    public void RequireMatchesRejectsAMismatchedName()
    {
        PluginSha256File file = PluginSha256File.Parse($"{Hash}  wrong-name.zip");

        Assert.Throws<LauncherUpdateException>(() => file.RequireMatches("edwards.hello-0.1.0.zip"));
    }

    [Fact]
    public void RequireMatchesAcceptsAnAbsentFileName()
    {
        PluginSha256File file = PluginSha256File.Parse(Hash);

        file.RequireMatches("edwards.hello-0.1.0.zip");
    }
}
