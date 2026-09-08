using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class ChatSettingsTests
{
    [Fact]
    public void Default_values_match_AceCharacterOptions2Default_stance()
    {
        var d = ChatSettings.Default;
        Assert.True(d.HearGeneralChat);
        Assert.True(d.HearTradeChat);
        Assert.True(d.HearLFGChat);
        Assert.False(d.HearRoleplayChat);
        Assert.False(d.HearSocietyChat);
        Assert.False(d.AppearOffline);
        Assert.True(d.ShowTimestamps);
        Assert.True(d.FilterProfanity);
        Assert.Equal(12f, d.FontSize);

        Assert.Equal(1.0f, d.DefaultOpacity);
        Assert.Equal(1.0f, d.ActiveOpacity);
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = ChatSettings.Default;
        var b = ChatSettings.Default with { HearTradeChat = false };
        var c = ChatSettings.Default with { HearTradeChat = false };
        Assert.NotEqual(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void With_expression_clones_one_field()
    {
        var d = ChatSettings.Default with { FontSize = 16f };
        Assert.Equal(16f, d.FontSize);
        Assert.True(d.HearGeneralChat);
        Assert.True(d.ShowTimestamps);
    }
}
