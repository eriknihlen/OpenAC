using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Profiles;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class LauncherPluginCompatibilityTests
{
    [Fact]
    public void CompatibleManifestReturnsNoReason()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0");

        Assert.Null(LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Gui,
            LauncherVersion.Parse("0.1.7")));
    }

    [Fact]
    public void NoInstalledClientIsStillInstallable()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0");

        Assert.Equal(
            LauncherPluginCompatibility.ClientNotInstalled,
            LauncherPluginCompatibility.Evaluate(manifest, LaunchMode.Gui, clientVersion: null));
    }

    [Fact]
    public void BelowMinHostVersionIsRefused()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.2.0");

        string? reason = LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Gui,
            LauncherVersion.Parse("0.1.7"));

        Assert.Equal("requires OpenAC 0.2.0 or newer (this is 0.1.7)", reason);
    }

    [Fact]
    public void AboveMaxHostVersionIsRefused()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", maxHostVersion: "0.1.0");

        string? reason = LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Gui,
            LauncherVersion.Parse("0.1.7"));

        Assert.Equal("supports OpenAC up to 0.1.0 (this is 0.1.7)", reason);
    }

    [Fact]
    public void ASkippedVersionIsRefused()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", skipHostVersions: ["0.1.7"]);

        string? reason = LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Gui,
            LauncherVersion.Parse("0.1.7"));

        Assert.Equal("is marked broken on OpenAC 0.1.7", reason);
    }

    [Fact]
    public void HeadlessOnlyManifestRefusesGraphicalLaunch()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["headless"]);

        string? reason = LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Gui,
            LauncherVersion.Parse("0.1.7"));

        Assert.Equal("runs only on the headless host", reason);
    }

    [Fact]
    public void GraphicalOnlyManifestRefusesHeadlessLaunch()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["graphical"]);

        string? reason = LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.Headless,
            LauncherVersion.Parse("0.1.7"));

        Assert.Equal("runs only on the graphical host", reason);
    }

    [Fact]
    public void GuiSelectCountsAsGraphical()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["graphical"]);

        Assert.Null(LauncherPluginCompatibility.Evaluate(
            manifest,
            LaunchMode.GuiSelect,
            LauncherVersion.Parse("0.1.7")));
    }

    [Fact]
    public void DescribeReportsCompatibleWithTheInstalledClientOnBothHosts()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0");

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, LauncherVersion.Parse("0.1.7"));

        Assert.Equal("Compatible with OpenAC 0.1.7", description.Text);
        Assert.False(description.IsWarning);
    }

    [Fact]
    public void DescribeReportsNoClientInstalledWithoutRunningTheHostCheck()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["headless"]);

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, clientVersion: null);

        Assert.Equal("Client not installed", description.Text);
        Assert.False(description.IsWarning);
    }

    [Fact]
    public void DescribeReportsGraphicalOnlyWhenTheHostRestrictionIsTheOnlyIssue()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["graphical"]);

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, LauncherVersion.Parse("0.1.7"));

        Assert.Equal("Graphical only", description.Text);
        Assert.False(description.IsWarning);
    }

    [Fact]
    public void DescribeReportsHeadlessOnlyWhenTheHostRestrictionIsTheOnlyIssue()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.1.0", hosts: ["headless"]);

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, LauncherVersion.Parse("0.1.7"));

        Assert.Equal("Headless only", description.Text);
        Assert.False(description.IsWarning);
    }

    [Fact]
    public void DescribeReportsTheReasonAsAWarningWhenBothHostsAreIncompatible()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.2.0");

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, LauncherVersion.Parse("0.1.7"));

        Assert.Equal("requires OpenAC 0.2.0 or newer (this is 0.1.7)", description.Text);
        Assert.True(description.IsWarning);
    }

    [Fact]
    public void DescribeReportsTheVersionReasonEvenWhenTheOnlySupportedHostAlsoFailsVersion()
    {
        LauncherPluginManifest manifest = Manifest(minHostVersion: "0.2.0", hosts: ["headless"]);

        LauncherPluginCompatibility.CompatibilityDescription description =
            LauncherPluginCompatibility.Describe(manifest, LauncherVersion.Parse("0.1.7"));

        Assert.Equal("requires OpenAC 0.2.0 or newer (this is 0.1.7)", description.Text);
        Assert.True(description.IsWarning);
    }

    private static LauncherPluginManifest Manifest(
        string minHostVersion,
        string? maxHostVersion = null,
        IReadOnlyList<string>? skipHostVersions = null,
        IReadOnlyList<string>? hosts = null)
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
        if (skipHostVersions is not null)
            fields.Add($"\"skipHostVersions\": [{string.Join(",", skipHostVersions.Select(v => $"\"{v}\""))}]");
        fields.Add(
            $"\"hosts\": [{string.Join(",", (hosts ?? ["graphical", "headless"]).Select(h => $"\"{h}\""))}]");

        return LauncherPluginManifest.Parse("{" + string.Join(",", fields) + "}");
    }
}
