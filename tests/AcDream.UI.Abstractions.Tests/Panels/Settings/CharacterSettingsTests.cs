using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class CharacterSettingsTests
{
    [Fact]
    public void Default_values_are_conservative()
    {
        var d = CharacterSettings.Default;
        Assert.Equal("Local", d.DefaultChatChannel);
        Assert.False(d.AutoAttack);
        Assert.True(d.ConfirmSalvage);
        Assert.True(d.ShowPickupMessages);
    }

    [Fact]
    public void AvailableChannels_includes_retail_routing_targets()
    {
        var list = CharacterSettings.AvailableChannels;
        Assert.Contains("Local", list);
        Assert.Contains("Allegiance", list);
        Assert.Contains("Fellowship", list);
        Assert.Contains("General", list);
        Assert.Contains("Trade", list);
        Assert.Contains("LFG", list);
        Assert.Contains("Roleplay", list);
    }

    [Fact]
    public void Equality_is_value_based()
    {
        var a = CharacterSettings.Default;
        var b = CharacterSettings.Default with { AutoAttack = true };
        var c = CharacterSettings.Default with { AutoAttack = true };
        Assert.NotEqual(a, b);
        Assert.Equal(b, c);
    }

    [Fact]
    public void With_expression_clones_one_field()
    {
        var d = CharacterSettings.Default with { DefaultChatChannel = "Allegiance" };
        Assert.Equal("Allegiance", d.DefaultChatChannel);
        Assert.False(d.AutoAttack);
    }
}
