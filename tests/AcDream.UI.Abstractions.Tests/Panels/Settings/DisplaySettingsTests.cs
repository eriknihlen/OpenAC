using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class DisplaySettingsTests
{
    [Fact]
    public void Default_values_match_normal_client_policy()
    {
        var d = DisplaySettings.Default;
        Assert.Equal("1280x720", d.Resolution);
        Assert.False(d.Fullscreen);
        Assert.True(d.VSync);
        Assert.Equal(90f, d.FieldOfView);
        Assert.Equal(1.0f, d.Gamma);
        Assert.False(d.ShowFps);
        Assert.Equal(RenderPackSelectionSettings.Retail, d.RenderPack);
        Assert.True(d.RenderPack.IsRetail);
    }

    [Fact]
    public void AvailableResolutions_includes_common_16_9_options()
    {
        var list = DisplaySettings.AvailableResolutions;
        Assert.Contains("1280x720", list);
        Assert.Contains("1920x1080", list);
        Assert.Contains("2560x1440", list);
        Assert.Contains("3840x2160", list);
        // List should be ascending so the dropdown reads naturally.
        for (int i = 1; i < list.Count; i++)
        {
            int prevW = ParseWidth(list[i - 1]);
            int curW  = ParseWidth(list[i]);
            Assert.True(curW >= prevW, $"Resolutions not sorted: {list[i - 1]} >= {list[i]}");
        }
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = DisplaySettings.Default;
        var b = DisplaySettings.Default with { Fullscreen = true };
        var c = DisplaySettings.Default with { Fullscreen = true };
        Assert.NotEqual(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void With_expression_clones_one_field()
    {
        var d = DisplaySettings.Default with { FieldOfView = 90f };
        Assert.Equal(90f, d.FieldOfView);
        // Other fields untouched.
        Assert.Equal("1280x720", d.Resolution);
        Assert.True(d.VSync);
        Assert.False(d.ShowFps);
    }

    [Fact]
    public void Render_pack_selection_uses_stable_logical_ids()
    {
        var selection = new RenderPackSelectionSettings(
            "acdream.atmospheric",
            "1.0.0",
            "medium");
        DisplaySettings changed = DisplaySettings.Default with
        {
            RenderPack = selection,
        };

        Assert.Equal("acdream.atmospheric", changed.RenderPack.PackId);
        Assert.Equal("1.0.0", changed.RenderPack.PackVersion);
        Assert.Equal("medium", changed.RenderPack.PresetId);
        Assert.False(changed.RenderPack.IsRetail);
        Assert.Equal(RenderPackSelectionSettings.Retail, DisplaySettings.Default.RenderPack);
    }

    [Fact]
    public void Render_pack_setting_overrides_are_value_equal_and_case_insensitive_by_id()
    {
        var first = new RenderPackSelectionSettings("pack", "1.0.0", "high")
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { ["Exposure"] = "1.25" }),
        };
        var second = new RenderPackSelectionSettings("pack", "1.0.0", "high")
        {
            SettingOverrides = new RenderPackSettingOverrides(
                new Dictionary<string, string> { ["exposure"] = "1.25" }),
        };

        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(first.SettingOverrides.TryGetValue("EXPOSURE", out string? value));
        Assert.Equal("1.25", value);
        Assert.Empty(RenderPackSelectionSettings.Retail.SettingOverrides);
    }

    private static int ParseWidth(string res)
    {
        int x = res.IndexOf('x');
        return int.Parse(res.AsSpan(0, x));
    }
}
