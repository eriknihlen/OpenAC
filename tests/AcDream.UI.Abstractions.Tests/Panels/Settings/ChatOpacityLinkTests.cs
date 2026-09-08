using AcDream.UI.Abstractions.Panels.Settings;

namespace AcDream.UI.Abstractions.Tests.Panels.Settings;

public sealed class ChatOpacityLinkTests
{
    [Fact]
    public void SetDefault_BelowCurrentActive_LeavesActiveUnchanged()
    {
        var (def, active) = ChatOpacityLink.SetDefault(currentActive: 1.0f, newDefault: 0.3f);
        Assert.Equal(0.3f, def);
        Assert.Equal(1.0f, active);
    }

    [Fact]
    public void SetDefault_AboveCurrentActive_DragsActiveUp_DoesNotClampDefault()
    {
        var (def, active) = ChatOpacityLink.SetDefault(currentActive: 0.4f, newDefault: 0.9f);
        Assert.Equal(0.9f, def);
        Assert.Equal(0.9f, active);
    }

    [Fact]
    public void SetActive_AboveCurrentDefault_LeavesDefaultUnchanged()
    {
        var (def, active) = ChatOpacityLink.SetActive(currentDefault: 0.2f, newActive: 0.9f);
        Assert.Equal(0.2f, def);
        Assert.Equal(0.9f, active);
    }

    [Fact]
    public void SetActive_BelowCurrentDefault_DragsDefaultDown()
    {
        var (def, active) = ChatOpacityLink.SetActive(currentDefault: 0.7f, newActive: 0.3f);
        Assert.Equal(0.3f, def);
        Assert.Equal(0.3f, active);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void SetDefault_ClampsInputToUnitRange(float rawInput, float expectedDefault)
    {
        var (def, _) = ChatOpacityLink.SetDefault(currentActive: 1f, newDefault: rawInput);
        Assert.Equal(expectedDefault, def);
    }

    [Theory]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 1f)]
    public void SetActive_ClampsInputToUnitRange(float rawInput, float expectedActive)
    {
        var (_, active) = ChatOpacityLink.SetActive(currentDefault: 0f, newActive: rawInput);
        Assert.Equal(expectedActive, active);
    }

    [Fact]
    public void SetDefault_ThenSetActive_NeverProducesActiveBelowDefault()
    {
        // A short sequence exercising the invariant through several moves, the
        // way a user dragging both sliders back and forth would.
        (float def, float active) = (0.5f, 1.0f);
        (def, active) = ChatOpacityLink.SetDefault(active, 0.9f);
        Assert.True(active >= def);
        (def, active) = ChatOpacityLink.SetActive(def, 0.1f);
        Assert.True(active >= def);
        (def, active) = ChatOpacityLink.SetDefault(active, 0.6f);
        Assert.True(active >= def);
        Assert.Equal(0.6f, def);
        Assert.Equal(0.6f, active);
    }
}
