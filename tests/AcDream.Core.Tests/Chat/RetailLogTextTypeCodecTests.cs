using AcDream.Core.Chat;

namespace AcDream.Core.Tests.Chat;

public sealed class RetailLogTextTypeCodecTests
{
    [Fact]
    public void AnOrdinaryValuePassesThroughUnchanged()
    {
        Assert.Equal(
            RetailLogTextType.Magic,
            RetailLogTextTypeCodec.FromPluginValue((int)RetailLogTextType.Magic));
    }

    [Fact]
    public void ClientLocalFallsBackToDefault()
    {
        Assert.Equal(
            RetailLogTextType.Default,
            RetailLogTextTypeCodec.FromPluginValue((int)RetailLogTextType.ClientLocal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(9999)]
    public void AnOutOfRangeValueFallsBackToDefault(int value)
    {
        Assert.Equal(
            RetailLogTextType.Default,
            RetailLogTextTypeCodec.FromPluginValue(value));
    }
}
