using AcDream.App.Rendering;
using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.App.Tests.Rendering;

public sealed class DisplayModeCatalogTests
{
    private static readonly (int W, int H) Desktop2560 = (2560, 1440);

    [Fact]
    public void Curate_DropsLegacyFormats_KeepsModernFamilies()
    {
        var modes = new (int, int)[]
        {
            (640, 480), (800, 600), (1024, 768), (1280, 1024),   // 4:3 / 5:4 legacy
            (1280, 720), (1366, 768), (1600, 900), (1920, 1080), // 16:9
            (1920, 1200),                                        // 16:10
            (2560, 1440),
        };

        var curated = DisplayModeCatalog.Curate(modes, Desktop2560);

        Assert.Equal(
            ["1280x720", "1366x768", "1600x900", "1920x1080", "1920x1200", "2560x1440"],
            curated);
    }

    [Fact]
    public void Curate_ExcludesModesLargerThanTheDesktop()
    {
        var curated = DisplayModeCatalog.Curate(
            [(1920, 1080), (2560, 1440), (3840, 2160)], Desktop2560);

        Assert.Equal(["1920x1080", "2560x1440"], curated);
    }

    [Fact]
    public void Curate_CollapsesRefreshRateDuplicates_AndSortsAscending()
    {
        var curated = DisplayModeCatalog.Curate(
            [(2560, 1440), (1920, 1080), (1920, 1080), (1920, 1080), (1280, 720)],
            Desktop2560);

        Assert.Equal(["1280x720", "1920x1080", "2560x1440"], curated);
    }

    [Fact]
    public void Curate_KeepsUltrawideFamilies()
    {
        var curated = DisplayModeCatalog.Curate(
            [(2560, 1080), (3440, 1440), (3840, 1080)], (3840, 1600));

        Assert.Equal(["2560x1080", "3440x1440", "3840x1080"], curated);
    }

    [Fact]
    public void Curate_AlwaysIncludesTheDesktopModeItself()
    {
        // The desktop mode is displayable by definition — it stays even when
        // its aspect matches no listed family (and it is the Defaults value).
        var curated = DisplayModeCatalog.Curate(
            [(1920, 1080), (1920, 1440)], (1920, 1440));   // desktop is 4:3!

        Assert.Contains("1920x1440", curated);
        Assert.Contains("1920x1080", curated);
    }

    [Fact]
    public void Curate_DropsSubMinimumWidths_EvenWhenWidescreen()
    {
        // 1024x576 is exactly 16:9 but below the modern floor.
        var curated = DisplayModeCatalog.Curate(
            [(1024, 576), (1280, 720)], Desktop2560);

        Assert.Equal(["1280x720"], curated);
    }

    [Fact]
    public void FallbackPresetLadder_ItselfPassesTheCurationRule()
    {
        // The static fixture-fallback list must never offer something the
        // production rule would reject (a big desktop accepts all of it).
        var parsed = DisplaySettings.AvailableResolutions
            .Select(static s => s.Split('x'))
            .Select(static p => (int.Parse(p[0]), int.Parse(p[1])));

        var curated = DisplayModeCatalog.Curate(parsed, (3840, 2160));

        Assert.Equal(DisplaySettings.AvailableResolutions, curated);
    }

    [Fact]
    public void FallbackDefault_IsAMemberOfTheFallbackList()
    {
        Assert.Contains(DisplaySettings.Default.Resolution, DisplaySettings.AvailableResolutions);
    }


    [Fact]
    public void BuildWindowedOffering_RdpStarvedModeList_GainsTheLadderSizesThatFit()
    {
        var offering = DisplayModeCatalog.BuildWindowedOffering(
            ["1920x1080", "2056x1290"], (2056, 1290));

        Assert.Equal(
            ["1280x720", "1366x768", "1600x900", "1920x1080", "2056x1290"],
            offering);
    }

    [Fact]
    public void BuildWindowedOffering_RichMonitor_IsTheDedupedUnion()
    {
        var offering = DisplayModeCatalog.BuildWindowedOffering(
            ["1280x720", "1366x768", "1600x900", "1920x1080", "1920x1200", "2560x1440"],
            Desktop2560);

        Assert.Equal(
            ["1280x720", "1366x768", "1600x900", "1920x1080", "1920x1200", "2560x1440"],
            offering);
    }

    [Fact]
    public void BuildWindowedOffering_LadderEntriesLargerThanTheDesktop_StayExcluded()
    {
        var offering = DisplayModeCatalog.BuildWindowedOffering(
            ["1600x900"], (1600, 900));

        Assert.Equal(["1280x720", "1366x768", "1600x900"], offering);
    }
}
