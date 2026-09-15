using System.Runtime.CompilerServices;
using AcDream.Core.Plugins;
using AcDream.Launcher.Core.Plugins;
using CorePluginHostKind = AcDream.Core.Plugins.PluginHostKind;
using CorePluginHostVersion = AcDream.Core.Plugins.PluginHostVersion;
using LauncherHostKind = AcDream.Launcher.Core.Plugins.LauncherPluginHostKind;

namespace AcDream.Headless.Tests;

/// <summary>Pins <see cref="LauncherPluginManifest"/>, the launcher's own <c>plugin.json</c> reader, to
/// <see cref="PluginManifest"/>, the client's, on every manifest in <c>samples/</c>, a MossTank-shaped
/// fixture, and a corpus covering the L-300-era host fields. Also pins
/// <see cref="LauncherPluginApiRange"/> to <see cref="AcDream.Plugin.Abstractions.PluginApi"/>
/// (L-303).</summary>
public sealed class PluginManifestParityTests
{
    [Fact]
    public void LauncherApiRangeMatchesPluginAbstractionsApiRange()
    {
        Assert.Equal(
            AcDream.Plugin.Abstractions.PluginApi.MinimumSupported,
            LauncherPluginApiRange.Minimum);
        Assert.Equal(
            AcDream.Plugin.Abstractions.PluginApi.Current,
            LauncherPluginApiRange.Current);
    }

    [Theory]
    [MemberData(nameof(SampleManifestPaths))]
    public void LauncherReaderAgreesWithCoreOnASampleManifest(string path)
    {
        AssertFieldsAgree(File.ReadAllText(path));
    }

    [Fact]
    public void LauncherReaderAgreesWithCoreOnAMossTankShapedManifest() =>
        AssertFieldsAgree("""
            {
              "id": "acdream.mosstank",
              "displayName": "MossTank",
              "version": "0.1.0",
              "entryDll": "AcDream.Plugins.MossTank.dll",
              "apiVersion": 1,
              "minHostVersion": "0.1.0",
              "hosts": ["graphical", "headless"]
            }
            """);

    [Theory]
    [MemberData(nameof(HostFieldCorpus))]
    public void LauncherReaderAgreesWithCoreOnHostFields(string json) => AssertFieldsAgree(json);

    [Theory]
    [InlineData("0.1.0", "0.2.0", null, null, "0.1.7", "graphical")]
    [InlineData("0.2.0", null, null, null, "0.1.7", "graphical")]
    [InlineData("0.1.0", "0.1.5", null, null, "0.1.7", "graphical")]
    [InlineData("0.1.0", null, "0.1.7", null, "0.1.7", "graphical")]
    [InlineData("0.1.0", null, null, "headless", "0.1.7", "graphical")]
    [InlineData("0.1.0", null, null, "graphical", "0.1.7", "headless")]
    public void CompatibilityReasonsAgreeWhenTheClientVersionIsKnown(
        string minHostVersion,
        string? maxHostVersion,
        string? skipHostVersion,
        string? onlyHost,
        string clientVersion,
        string requestedHost)
    {
        string json = ManifestJson(minHostVersion, maxHostVersion, skipHostVersion, onlyHost);

        PluginManifest coreManifest = PluginManifest.Parse(json);
        LauncherPluginManifest launcherManifest = LauncherPluginManifest.Parse(json);

        CorePluginHostKind coreHost = requestedHost == "headless"
            ? CorePluginHostKind.Headless
            : CorePluginHostKind.Graphical;
        LauncherHostKind launcherHost = requestedHost == "headless"
            ? LauncherHostKind.Headless
            : LauncherHostKind.Graphical;

        string? coreReason = PluginHostCompatibility.Evaluate(
            coreManifest,
            coreHost,
            CorePluginHostVersion.FromInformationalVersion(clientVersion));
        string? launcherReason = LauncherPluginCompatibility.Evaluate(
            launcherManifest,
            requestedHost == "headless"
                ? AcDream.Launcher.Core.Profiles.LaunchMode.Headless
                : AcDream.Launcher.Core.Profiles.LaunchMode.Gui,
            AcDream.Launcher.Core.Updates.LauncherVersion.Parse(clientVersion));

        Assert.Equal(coreReason, launcherReason);
    }

    [Theory]
    [MemberData(nameof(InvalidManifestCorpus))]
    public void BothReadersRejectAnInvalidManifest(string json)
    {
        Assert.Throws<PluginManifestException>(() => PluginManifest.Parse(json));
        Assert.Throws<LauncherPluginManifestException>(() => LauncherPluginManifest.Parse(json));
    }

    /// <summary>Both readers reject a repeated property name anywhere in the document, with the
    /// same message (L-311).</summary>
    [Theory]
    [MemberData(nameof(DuplicatePropertyCorpus))]
    public void BothReadersRejectADuplicatePropertyWithTheSameMessage(string json, string expectedMessage)
    {
        PluginManifestException coreError = Assert.Throws<PluginManifestException>(
            () => PluginManifest.Parse(json));
        LauncherPluginManifestException launcherError = Assert.Throws<LauncherPluginManifestException>(
            () => LauncherPluginManifest.Parse(json));

        Assert.Equal(expectedMessage, coreError.Message);
        Assert.Equal(expectedMessage, launcherError.Message);
    }

    [Fact]
    public void BothReadersAllowTheSamePropertyNameInSiblingObjects()
    {
        const string json = """
            {
              "id": "edwards.hello",
              "displayName": "Hello",
              "version": "0.1.0",
              "entryDll": "Hello.dll",
              "apiVersion": 1,
              "a": { "name": "a" },
              "b": { "name": "b" }
            }
            """;

        PluginManifest core = PluginManifest.Parse(json);
        LauncherPluginManifest launcher = LauncherPluginManifest.Parse(json);

        Assert.Equal(core.Id, launcher.Id);
    }

    public static TheoryData<string, string> DuplicatePropertyCorpus()
    {
        return new TheoryData<string, string>
        {
            {
                """{"id":"edwards.hello","id":"edwards.other","displayName":"Hello","version":"0.1.0","entryDll":"Hello.dll","apiVersion":1}""",
                "duplicate property: id"
            },
            {
                """{"Id":"edwards.hello","id":"edwards.other","displayName":"Hello","version":"0.1.0","entryDll":"Hello.dll","apiVersion":1}""",
                "duplicate property: id"
            },
            {
                """{"id":"edwards.hello","displayName":"Hello","version":"0.1.0","entryDll":"Hello.dll","apiVersion":1,"extra":{"name":"a","name":"b"}}""",
                "duplicate property: name"
            },
            {
                """{"id":"edwards.hello","displayName":"Hello","version":"0.1.0","entryDll":"Hello.dll","apiVersion":1,"extra":[{"name":"a","name":"b"}]}""",
                "duplicate property: name"
            },
        };
    }

    public static TheoryData<string> InvalidManifestCorpus()
    {
        return new TheoryData<string>
        {
            ManifestJson("0.1.0", null, null, hosts: ""),
            ManifestJson("0.1.0", null, null, "bogus-host"),
            ManifestJson("01.0.0", null, null, null),
            ManifestJson("v1.0.0", null, null, null),
            ManifestJson("1.0", null, null, null),
            ManifestJson("1.0.0-beta", null, null, null),
            ManifestJson("0.2.0", "0.1.0", null, null),
        };
    }

    public static TheoryData<string> SampleManifestPaths()
    {
        string root = FindRepositoryRoot();
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(root, "samples"),
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            data.Add(path);
        }

        return data;
    }

    public static TheoryData<string> HostFieldCorpus()
    {
        var data = new TheoryData<string>
        {
            ManifestJson("0.1.0", null, null, null),
            ManifestJson("0.1.0", "0.2.0", null, null),
            ManifestJson("0.1.0", "0.2.0", "0.1.5", null),
            ManifestJson("0.1.0", null, null, "graphical"),
            ManifestJson("0.1.0", null, null, "headless"),
            ManifestJson("0.1.0", null, null, "graphical,headless"),
        };
        return data;
    }

    private static string ManifestJson(
        string minHostVersion,
        string? maxHostVersion,
        string? skipHostVersion,
        string? hosts)
    {
        var fields = new List<string>
        {
            "\"id\": \"edwards.hello\"",
            "\"displayName\": \"Hello\"",
            "\"version\": \"0.1.0\"",
            "\"entryDll\": \"Hello.dll\"",
            "\"apiVersion\": 1",
            $"\"minHostVersion\": \"{minHostVersion}\"",
        };
        if (maxHostVersion is not null)
            fields.Add($"\"maxHostVersion\": \"{maxHostVersion}\"");
        if (skipHostVersion is not null)
            fields.Add($"\"skipHostVersions\": [\"{skipHostVersion}\"]");
        if (hosts is not null)
        {
            string joined = string.Join(
                ",",
                hosts.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => $"\"{h}\""));
            fields.Add($"\"hosts\": [{joined}]");
        }

        return "{" + string.Join(",", fields) + "}";
    }

    private static void AssertFieldsAgree(string json)
    {
        PluginManifest core = PluginManifest.Parse(json);
        LauncherPluginManifest launcher = LauncherPluginManifest.Parse(json);

        Assert.Equal(core.Id, launcher.Id);
        Assert.Equal(core.DisplayName, launcher.DisplayName);
        Assert.Equal(core.Version, launcher.Version);
        Assert.Equal(core.EntryDll, launcher.EntryDll);
        Assert.Equal(core.ApiVersion, launcher.ApiVersion);
        Assert.Equal(
            core.Kinds.Select(kind => kind.ToString()),
            launcher.Kinds.Select(kind => kind.ToString()));
        Assert.Equal(core.MinHostVersion?.ToString(), launcher.MinHostVersion?.ToString());
        Assert.Equal(core.MaxHostVersion?.ToString(), launcher.MaxHostVersion?.ToString());
        Assert.Equal(
            core.SkipHostVersions.Select(version => version.ToString()),
            launcher.SkipHostVersions.Select(version => version.ToString()));
        Assert.Equal(
            CoreHostNames(core.Hosts, declared: launcher.Hosts is not null),
            launcher.Hosts?.Select(host => host.ToString()) ?? []);
    }

    private static IEnumerable<string> CoreHostNames(
        IReadOnlyList<CorePluginHostKind> hosts,
        bool declared) =>
        declared ? hosts.Select(host => host.ToString()) : [];

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourcePath = "")
    {
        string[] starts =
        {
            Path.GetDirectoryName(sourcePath) ?? string.Empty,
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory,
        };
        foreach (string start in starts)
        {
            if (string.IsNullOrEmpty(start))
            {
                continue;
            }

            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AcDream.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find AcDream.slnx above the source, working, or output directory.");
    }
}
