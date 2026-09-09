using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class AudioSettingsTests
{
    [Fact]
    public void Default_values_match_engine_constructor_defaults()
    {
        var d = AudioSettings.Default;
        Assert.Equal(1.0f, d.Master);
        Assert.Equal(1.0f, d.Sfx);
        Assert.Equal(1.0f, d.Ambient);
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = AudioSettings.Default;
        var b = AudioSettings.Default with { Master = 0.5f };
        var c = AudioSettings.Default with { Master = 0.5f };
        Assert.NotEqual(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void With_expression_clones_one_field()
    {
        var d = AudioSettings.Default with { Ambient = 0.25f };
        Assert.Equal(0.25f, d.Ambient);
        // Other fields untouched.
        Assert.Equal(AudioSettings.Default.Master, d.Master);
        Assert.Equal(AudioSettings.Default.Sfx, d.Sfx);
    }
}
