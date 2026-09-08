using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

public class PluginManifestTests
{
    [Fact]
    public void Parse_ValidManifest_ReturnsManifest()
    {
        const string json = """
        {
          "id": "acdream.mosstank",
          "displayName": "MossTank",
          "version": "0.1.0",
          "entryDll": "AcDream.Plugins.MossTank.dll",
          "apiVersion": 1
        }
        """;

        var manifest = PluginManifest.Parse(json);

        Assert.Equal("acdream.mosstank", manifest.Id);
        Assert.Equal("MossTank", manifest.DisplayName);
        Assert.Equal("0.1.0", manifest.Version);
        Assert.Equal("AcDream.Plugins.MossTank.dll", manifest.EntryDll);
        Assert.Equal(1, manifest.ApiVersion);
        Assert.Equal([PluginKind.Gameplay], manifest.Kinds);
    }

    [Fact]
    public void Parse_MissingRequiredField_Throws()
    {
        const string json = """
        { "id": "x", "version": "0.1.0", "entryDll": "x.dll", "apiVersion": 1 }
        """;

        var ex = Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(json));
        Assert.Equal("missing required field: displayName", ex.Message);
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        Assert.Throws<PluginManifestException>(() => PluginManifest.Parse("{ not json"));
    }

    [Fact]
    public void Parse_EmptyDependencies_DefaultsToEmptyList()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "0.1.0",
          "entryDll": "x.dll",
          "apiVersion": 1
        }
        """;

        var manifest = PluginManifest.Parse(json);
        Assert.Empty(manifest.Dependencies);
    }

    [Fact]
    public void Parse_RenderPackAndHybridKinds_AreExplicitAndDeduplicated()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "kinds": ["renderPack", "gameplay", "RENDERPACK"]
        }
        """;

        PluginManifest manifest = PluginManifest.Parse(json);

        Assert.Equal(
            [PluginKind.RenderPack, PluginKind.Gameplay],
            manifest.Kinds);
        Assert.True(manifest.Declares(PluginKind.RenderPack));
        Assert.True(manifest.Declares(PluginKind.Gameplay));
    }

    [Theory]
    [InlineData("[]", "kinds must contain at least one entry")]
    [InlineData("[\"nativeCode\"]", "unknown plugin kind: nativeCode")]
    public void Parse_InvalidKinds_Throws(string kindsJson, string expected)
    {
        string json = $$"""
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "kinds": {{kindsJson}}
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal(expected, error.Message);
    }
}
