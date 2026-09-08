using System.Numerics;
using AcDream.UI.Abstractions.Panels.Chat;
using Xunit;

namespace AcDream.UI.Abstractions.Tests.Panels.Chat;

public sealed class RetailChatColorTableTests
{
    [Theory]
    [InlineData(0x00u, 0.5f, 1f, 0.498f, 1f)]      // Default (default-fill)
    [InlineData(0x01u, 0.5f, 1f, 0.498f, 1f)]      // All (default-fill)
    [InlineData(0x02u, 1f, 1f, 1f, 1f)]
    [InlineData(0x03u, 1f, 1f, 0.247f, 1f)]        // Tell — yellow
    [InlineData(0x04u, 0.824f, 0.824f, 0.392f, 1f)] // Speech_Direct_Send — dark yellow
    [InlineData(0x05u, 1f, 0.498f, 1f, 1f)]
    [InlineData(0x06u, 1f, 0.247f, 0.247f, 1f)]
    [InlineData(0x07u, 0.247f, 0.749f, 1f, 1f)]
    [InlineData(0x08u, 1f, 0.588f, 0.588f, 1f)]
    [InlineData(0x09u, 1f, 0.588f, 0.588f, 1f)]
    [InlineData(0x0Au, 1f, 1f, 0.247f, 1f)]        // Social — yellow
    [InlineData(0x0Bu, 0.824f, 0.824f, 0.392f, 1f)] // Social_Send — dark yellow
    [InlineData(0x0Cu, 0.824f, 0.824f, 0.784f, 1f)]
    [InlineData(0x0Du, 0.247f, 0.863f, 0.863f, 1f)]
    [InlineData(0x0Eu, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x0Fu, 1f, 0.247f, 0.247f, 1f)]
    [InlineData(0x10u, 0.5f, 1f, 0.498f, 1f)]      // Appraisal (default-fill)
    [InlineData(0x11u, 0.247f, 0.749f, 1f, 1f)]
    [InlineData(0x12u, 0.933f, 0.573f, 0.118f, 1f)] // Allegiance — orange
    [InlineData(0x13u, 1f, 1f, 0.247f, 1f)]        // Fellowship — yellow
    [InlineData(0x14u, 0.5f, 1f, 0.498f, 1f)]      // World_Broadcast (default-fill)
    [InlineData(0x15u, 1f, 0.247f, 0.247f, 1f)]
    [InlineData(0x16u, 0.96f, 0.459f, 0.447f, 1f)]
    [InlineData(0x17u, 0.5f, 1f, 0.498f, 1f)]      // Recall (default-fill)
    [InlineData(0x18u, 0.5f, 1f, 0.498f, 1f)]
    [InlineData(0x19u, 0.5f, 1f, 0.498f, 1f)]      // Salvaging (default-fill)
    [InlineData(0x1Au, 1f, 0f, 0f, 1f)]
    [InlineData(0x1Bu, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x1Cu, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x1Du, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x1Eu, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x1Fu, 1f, 1f, 0.247f, 1f)]        // Admin_Tell — yellow
    [InlineData(0x20u, 0.706f, 0.863f, 0.941f, 1f)]
    [InlineData(0x21u, 0.933f, 0.573f, 0.118f, 1f)] // reserved — orange
    public void TryGetColor_PinsExactRetailFloat(
        uint logTextType, float r, float g, float b, float a)
    {
        Assert.True(RetailChatColorTable.TryGetColor(logTextType, out Vector4 color));
        Assert.Equal(new Vector4(r, g, b, a), color);
    }

    [Fact]
    public void Colors_HasExactlyThirtyFourEntries()
    {
        Assert.Equal(34, RetailChatColorTable.Colors.Count);
    }

    [Theory]
    [InlineData(0x00u)]
    [InlineData(0x01u)]
    [InlineData(0x10u)]
    [InlineData(0x14u)]
    [InlineData(0x17u)]
    [InlineData(0x18u)]
    [InlineData(0x19u)]
    public void SevenUnoverwrittenSlots_StayDefaultGreen(uint logTextType)
    {
        RetailChatColorTable.TryGetColor(logTextType, out Vector4 color);
        Assert.Equal(new Vector4(0.5f, 1f, 0.498f, 1f), color);
    }

    [Theory]
    [InlineData(0x22u)]   // one past the last valid slot
    [InlineData(0x23u)]
    [InlineData(100u)]
    [InlineData(uint.MaxValue)]
    public void TryGetColor_OutOfRange_ReturnsFalse(uint logTextType)
    {
        Assert.False(RetailChatColorTable.TryGetColor(logTextType, out _));
    }

    [Fact]
    public void EveryAlphaChannelIsOne()
    {
        foreach (Vector4 color in RetailChatColorTable.Colors)
            Assert.Equal(1f, color.W);
    }
}
