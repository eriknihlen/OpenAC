using AcDream.Launcher.Core.Updates;
using AcDream.Platform;

namespace AcDream.Launcher.Tests;

public sealed class LauncherStartupOptionsTests
{
    [Fact]
    public void ExplicitIsolationRootsAreNormalizedAndNeverResolveCanonicalPaths()
    {
        string root = Path.Combine(Path.GetTempPath(), "acdream-la11 options", "..", "isolation");
        string config = Path.Combine(root, "config") + Path.DirectorySeparatorChar;
        string data = Path.Combine(root, "data", ".", "state");
        string cache = Path.Combine(root, "cache") + Path.DirectorySeparatorChar;
        bool defaultResolverCalled = false;

        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            [
                "--config-dir", config,
                "--data-dir", data,
                "--cache-dir", cache,
                "--update-manifest-uri", "http://127.0.0.1:43119/manifest.json",
            ],
            () =>
            {
                defaultResolverCalled = true;
                throw new InvalidOperationException("canonical path resolver was touched");
            });

        Assert.False(defaultResolverCalled);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(config)),
            options.Paths.ConfigDirectory);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)),
            options.Paths.DataDirectory);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(cache)),
            options.Paths.CacheDirectory);
        Assert.Null(options.Paths.LegacyConfigDirectory);
        Assert.Equal(
            "http://127.0.0.1:43119/manifest.json",
            options.UpdateManifestUri.AbsoluteUri);
        Assert.Equal(LauncherStartupMode.Desktop, options.Mode);
    }

    [Fact]
    public void NoOverridesResolveDefaultsExactlyOnce()
    {
        var expected = new ApplicationPathSet("config", "data", "cache", "legacy");
        int calls = 0;

        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            [],
            () =>
            {
                calls++;
                return expected;
            });

        Assert.Same(expected, options.Paths);
        Assert.Equal(1, calls);
        Assert.Equal(ReleaseManifestClient.ProductionManifestUri, options.UpdateManifestUri);
    }

    [Fact]
    public void VerifyPublishIsExclusiveAndDoesNotResolvePaths()
    {
        int calls = 0;

        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            ["--verify-publish"],
            () =>
            {
                calls++;
                throw new InvalidOperationException();
            });

        Assert.Equal(LauncherStartupMode.VerifyPublish, options.Mode);
        Assert.Equal(0, calls);
        Assert.Throws<LauncherStartupOptionsException>(() =>
            LauncherStartupOptions.Parse(
                ["--verify-publish", "--cache-dir", Path.GetTempPath()]));
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void RejectsInvalidPublicArguments(string[] arguments)
    {
        Assert.Throws<LauncherStartupOptionsException>(() =>
            LauncherStartupOptions.Parse(
                arguments,
                () => new ApplicationPathSet("c", "d", "x", null)));
    }

    [Theory]
    [InlineData("https://updates.example.test/manifest.json")]
    [InlineData("http://localhost:8123/manifest.json")]
    [InlineData("http://[::1]:8123/manifest.json")]
    public void AcceptsHttpsAndLoopbackHttpFeeds(string value)
    {
        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            ["--update-manifest-uri", value],
            () => new ApplicationPathSet("c", "d", "x", null));

        Assert.Equal(new Uri(value), options.UpdateManifestUri);
    }

    [Fact]
    public void SelfUpdatePrefixesRetainOnlyTheValidatedPublicSuffix()
    {
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "acdream-la11-self"));
        string[] suffix =
        [
            "--config-dir", Path.Combine(root, "config"),
            "--data-dir", Path.Combine(root, "data"),
            "--cache-dir", Path.Combine(root, "cache"),
            "--update-manifest-uri", "http://localhost:8123/manifest.json",
        ];
        string[] helper =
        [
            LauncherSelfUpdateBootstrap.HelperArgument,
            "123",
            root,
            "0123456789abcdef0123456789abcdef",
            .. suffix,
        ];
        string[] confirmation =
        [
            LauncherSelfUpdateBootstrap.ConfirmArgument,
            "0123456789abcdef0123456789abcdef",
            .. suffix,
        ];

        LauncherStartupOptions helperOptions = LauncherStartupOptions.Parse(helper);
        LauncherStartupOptions confirmationOptions =
            LauncherStartupOptions.Parse(confirmation);

        Assert.Equal(LauncherStartupMode.SelfUpdateHelper, helperOptions.Mode);
        Assert.Equal(
            LauncherStartupMode.SelfUpdateConfirmation,
            confirmationOptions.Mode);
        Assert.Equal(suffix, helperOptions.PublicArguments);
        Assert.Equal(suffix, confirmationOptions.PublicArguments);
        Assert.Equal(helperOptions.Paths, confirmationOptions.Paths);
        Assert.Equal(helperOptions.UpdateManifestUri, confirmationOptions.UpdateManifestUri);
    }

    [Fact]
    public void LegacyDeferredSelfUpdatePrefixIsRejectedAsUntrustedInput()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-la11-deferred"));
        string[] suffix =
        [
            "--config-dir", Path.Combine(root, "config"),
            "--data-dir", Path.Combine(root, "data"),
            "--cache-dir", Path.Combine(root, "cache"),
            "--update-manifest-uri", "http://127.0.0.1:43119/manifest.json",
        ];

        Assert.Throws<LauncherStartupOptionsException>(() =>
            LauncherStartupOptions.Parse(
                ["--acdream-self-update-deferred-v1", .. suffix],
                () => throw new InvalidOperationException(
                    "canonical path resolver was touched")));
    }

    [Fact]
    public void AvaloniaCompositionRetainsTheExactParsedOptionsAndPathSet()
    {
        string root = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "acdream-la11-app-composition"));
        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            [
                "--config-dir", Path.Combine(root, "config"),
                "--data-dir", Path.Combine(root, "data"),
                "--cache-dir", Path.Combine(root, "cache"),
                "--update-manifest-uri", "https://updates.example.test/manifest.json",
            ],
            () => throw new InvalidOperationException(
                "canonical path resolver was touched"));

        var app = new App(options);

        Assert.Same(options, app.StartupOptions);
        Assert.Same(options.Paths, app.StartupOptions.Paths);
        Assert.Equal(
            new Uri("https://updates.example.test/manifest.json"),
            app.StartupOptions.UpdateManifestUri);
    }

    [Fact]
    public void CompositionRejectsAnyBootstrapArgumentDrift()
    {
        var paths = new ApplicationPathSet("config", "data", "cache", null);
        LauncherStartupOptions options = LauncherStartupOptions.Parse(
            ["--update-manifest-uri", "https://updates.example.test/manifest.json"],
            () => paths);

        Program.RequireUnchangedPublicArguments(
            options,
            new SelfUpdateStartupResult(
                false,
                0,
                options.PublicArguments.ToArray()));
        Assert.Throws<InvalidOperationException>(() =>
            Program.RequireUnchangedPublicArguments(
                options,
                new SelfUpdateStartupResult(
                    false,
                    0,
                    ["--update-manifest-uri", "https://other.example.test/manifest.json"])));
    }

    public static TheoryData<string[]> InvalidArguments()
    {
        string absolute = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "acdream-la11"));
        var data = new TheoryData<string[]>();
        data.Add(["--unknown", "value"]);
        data.Add(["--config-dir"]);
        data.Add(["--config-dir", "relative"]);
        data.Add(["--config-dir", absolute]);
        data.Add(
        [
            "--config-dir", absolute,
            "--data-dir", absolute,
        ]);
        data.Add(
        [
            "--config-dir", absolute,
            "--data-dir", absolute,
            "--cache-dir", absolute,
            "--cache-dir", absolute,
        ]);
        data.Add(
            ["--update-manifest-uri", "http://updates.example.test/manifest.json"]);
        data.Add(["--update-manifest-uri", "file:///tmp/manifest.json"]);
        data.Add(
            ["--update-manifest-uri", "https://user:secret@example.test/manifest.json"]);
        data.Add(
            ["--update-manifest-uri", "https://example.test/manifest.json?token=secret"]);
        data.Add(
            ["--update-manifest-uri", "https://example.test/manifest.json#fragment"]);
        data.Add(["--update-manifest-uri", "not-a-uri"]);
        data.Add(
        [
            "--update-manifest-uri", "https://example.test/a",
            "--update-manifest-uri", "https://example.test/b",
        ]);
        return data;
    }
}
