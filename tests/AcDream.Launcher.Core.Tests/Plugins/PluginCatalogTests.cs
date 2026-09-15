using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginCatalogTests
{
    [Fact]
    public void ParsesPluginsAndBlockedEntries()
    {
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "someone.bad", "versions": ["*"], "reason": "Sends chat spam." }
              ]
            }
            """);

        Assert.Equal(1, catalog.SchemaVersion);
        PluginCatalogEntry entry = Assert.Single(catalog.Plugins);
        Assert.Equal("edwards.hello", entry.Id);
        Assert.Equal("shaneedwards/openac-plugin-hello", entry.Repo);
        PluginBlock block = Assert.Single(catalog.Blocked);
        Assert.Equal("someone.bad", block.Id);
        Assert.True(block.Matches("9.9.9"));
    }

    [Fact]
    public void BlockedIsOptional()
    {
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ]
            }
            """);

        Assert.Empty(catalog.Blocked);
    }

    [Fact]
    public void RejectsAnUnknownTopLevelMember()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "extra": true
            }
            """));
    }

    [Fact]
    public void RejectsADuplicateProperty()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsACaseVariantDuplicateProperty()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "SchemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsADuplicatePropertyInsideAnArrayEntry()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "Id": "edwards.other", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsAnUnsupportedSchemaVersion()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 2,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ]
            }
            """));
    }

    [Theory]
    [InlineData("openac-plugin-hello")]
    [InlineData("shaneedwards/")]
    [InlineData("/openac-plugin-hello")]
    [InlineData("shane edwards/openac-plugin-hello")]
    [InlineData("shaneedwards/openac plugin hello")]
    [InlineData("-shaneedwards/openac-plugin-hello")]
    [InlineData("shane--edwards/openac-plugin-hello")]
    [InlineData("shaneedwards/..")]
    [InlineData("shaneedwards/.")]
    public void RejectsABadRepo(string repo)
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse($$"""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "{{repo}}" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsARepoWithATrailingNewline()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello\n" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsADuplicatePluginId()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" },
                { "id": "edwards.hello", "name": "Hello Again", "author": "Shane Edwards",
                  "description": "Also says hello.", "repo": "shaneedwards/openac-plugin-hello-2" }
              ]
            }
            """));
    }

    [Fact]
    public void RejectsACaseVariantDuplicatePluginId()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" },
                { "id": "Edwards.Hello", "name": "Hello Again", "author": "Shane Edwards",
                  "description": "Also says hello.", "repo": "shaneedwards/openac-plugin-hello-2" }
              ]
            }
            """));
    }

    [Fact]
    public void IsBlockedIgnoresCase()
    {
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "someone.bad", "versions": ["*"], "reason": "Sends chat spam." }
              ]
            }
            """);

        Assert.True(catalog.IsBlocked("Someone.Bad", version: null));
    }

    [Fact]
    public void IsBlockedMatchesWildcardAndExactVersions()
    {
        PluginCatalog catalog = PluginCatalog.Parse("""
            {
              "schemaVersion": 1,
              "plugins": [
                { "id": "edwards.hello", "name": "Hello", "author": "Shane Edwards",
                  "description": "Says hello.", "repo": "shaneedwards/openac-plugin-hello" }
              ],
              "blocked": [
                { "id": "edwards.hello", "versions": ["0.1.0"], "reason": "test" }
              ]
            }
            """);

        Assert.True(catalog.IsBlocked("edwards.hello", LauncherVersion.Parse("0.1.0")));
        Assert.False(catalog.IsBlocked("edwards.hello", LauncherVersion.Parse("0.1.1")));
        Assert.False(catalog.IsBlocked("someone.else", LauncherVersion.Parse("0.1.0")));
        Assert.True(catalog.IsBlocked("edwards.hello", version: null));
    }
}
