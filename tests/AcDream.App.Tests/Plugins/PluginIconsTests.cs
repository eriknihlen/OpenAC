using AcDream.Plugin.Abstractions;
using Xunit;

namespace AcDream.App.Tests.Plugins;

public sealed class PluginIconsTests
{
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(7735u, 0x06001E37u)]           // Decal-style bare index -> RenderSurface DID
    [InlineData(0x00FFFFFFu, 0x06FFFFFFu)]     // largest bare index (just below the boundary) -> still gets the block prefix
    [InlineData(0x01000000u, 0x01000000u)]     // exactly at the boundary -> passes through unchanged
    [InlineData(0x02000000u, 0x02000000u)]
    [InlineData(0x06002D14u, 0x06002D14u)]     // already a RenderSurface DID -> unchanged
    [InlineData(0x0600FFFFu, 0x0600FFFFu)]     // already at/above the block -> unchanged
    public void Normalize_MapsAccordingToTheGrammar(uint input, uint expected)
    {
        Assert.Equal(expected, PluginIcons.Normalize(input));
    }
}
