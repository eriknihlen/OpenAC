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

    [Fact]
    public void Parse_HostFields_AreRead()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "minHostVersion": "0.1.5",
          "maxHostVersion": "0.2.0",
          "skipHostVersions": ["0.1.7"],
          "hosts": ["headless"]
        }
        """;

        PluginManifest manifest = PluginManifest.Parse(json);

        Assert.Equal(new PluginHostVersion(0, 1, 5), manifest.MinHostVersion);
        Assert.Equal(new PluginHostVersion(0, 2, 0), manifest.MaxHostVersion);
        Assert.Equal([new PluginHostVersion(0, 1, 7)], manifest.SkipHostVersions);
        Assert.Equal([PluginHostKind.Headless], manifest.Hosts);
    }

    [Fact]
    public void Parse_HostFieldsAbsent_DefaultToAnyVersionBothHosts()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1
        }
        """;

        PluginManifest manifest = PluginManifest.Parse(json);

        Assert.Null(manifest.MinHostVersion);
        Assert.Null(manifest.MaxHostVersion);
        Assert.Empty(manifest.SkipHostVersions);
        Assert.Equal(
            [PluginHostKind.Graphical, PluginHostKind.Headless],
            manifest.Hosts);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("01.0.0")]
    public void Parse_MalformedHostVersion_Throws(string version)
    {
        string json = $$"""
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "minHostVersion": "{{version}}"
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal($"malformed minHostVersion: {version}", error.Message);
    }

    [Fact]
    public void Parse_MinAboveMax_Throws()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "minHostVersion": "0.2.0",
          "maxHostVersion": "0.1.5"
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("minHostVersion (0.2.0) is greater than maxHostVersion (0.1.5)", error.Message);
    }

    [Fact]
    public void Parse_EmptyHosts_Throws()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "hosts": []
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("hosts must contain at least one entry", error.Message);
    }

    [Fact]
    public void Parse_UnknownHost_Throws()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "hosts": ["mobile"]
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("unknown host: mobile", error.Message);
    }

    [Fact]
    public void Parse_DuplicateTopLevelKey_Throws()
    {
        const string json = """
        {
          "id": "x",
          "id": "y",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("duplicate property: id", error.Message);
    }

    [Fact]
    public void Parse_CaseVariantDuplicateKey_Throws()
    {
        const string json = """
        {
          "Id": "x",
          "id": "y",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("duplicate property: id", error.Message);
    }

    [Fact]
    public void Parse_DuplicateKeyInNestedObject_Throws()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "extra": { "name": "a", "name": "b" }
        }
        """;

        PluginManifestException error = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));

        Assert.Equal("duplicate property: name", error.Message);
    }

    [Fact]
    public void Parse_SameKeyInSiblingObjects_IsAllowed()
    {
        const string json = """
        {
          "id": "x",
          "displayName": "X",
          "version": "1.0.0",
          "entryDll": "x.dll",
          "apiVersion": 1,
          "a": { "name": "a" },
          "b": { "name": "b" }
        }
        """;

        var manifest = PluginManifest.Parse(json);

        Assert.Equal("x", manifest.Id);
    }
}
