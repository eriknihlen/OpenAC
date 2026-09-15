using AcDream.Launcher.Core.Plugins;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class LauncherPluginManifestTests
{
    private const string ValidJson = """
        {
          "id": "edwards.hello",
          "displayName": "Hello",
          "version": "0.1.0",
          "entryDll": "Hello.dll",
          "apiVersion": 1,
          "minHostVersion": "0.1.7",
          "hosts": ["graphical", "headless"]
        }
        """;

    [Fact]
    public void ParsesAllFields()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ValidJson);

        Assert.Equal("edwards.hello", manifest.Id);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal("Hello.dll", manifest.EntryDll);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal(new LauncherPluginHostVersion(0, 1, 7), manifest.MinHostVersion);
        Assert.Equal(
            [LauncherPluginHostKind.Graphical, LauncherPluginHostKind.Headless],
            manifest.Hosts);
        manifest.ValidateForInstall();
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "somethingNew": "value"
            }
            """);

        Assert.Equal("edwards.hello", manifest.Id);
    }

    [Fact]
    public void ValidateForInstallRejectsAnIdWithATrailingNewline()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"edwards.hello\"", "\"edwards.hello\\n\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("Edwards.Hello")]
    [InlineData("edwards.")]
    [InlineData(".hello")]
    [InlineData("edwards..hello")]
    public void ValidateForInstallRejectsABadId(string id)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"edwards.hello\"", $"\"{id}\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    public void ValidateForInstallRejectsANonSemVerVersion(string version)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"0.1.0\"", $"\"{version}\""));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRequiresMinHostVersion()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "hosts": ["graphical"]
            }
            """);

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRequiresHosts()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.7"
            }
            """);

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void ValidateForInstallRejectsAnUnsupportedApiVersion()
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(
            ValidJson.Replace("\"apiVersion\": 1", "\"apiVersion\": 2"));

        Assert.Throws<LauncherPluginManifestException>(manifest.ValidateForInstall);
    }

    [Fact]
    public void RejectsMinHostVersionAboveMaxHostVersion()
    {
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse("""
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "minHostVersion": "0.2.0",
              "maxHostVersion": "0.1.0"
            }
            """));
    }

    [Theory]
    [InlineData("v0.1.0", true)]
    [InlineData("v0.1.1", false)]
    [InlineData("0.1.0", false)]
    public void MatchesTagRequiresAVLeadingVersion(string tag, bool expected)
    {
        LauncherPluginManifest manifest = LauncherPluginManifest.Parse(ValidJson);

        Assert.Equal(expected, manifest.MatchesTag(tag));
    }
}
