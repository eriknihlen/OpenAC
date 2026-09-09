using AcDream.Core.Items;
using Xunit;

namespace AcDream.Core.Tests.Items;

public sealed class ClientObjectNameTests
{
    [Fact]
    public void AppropriateName_usesWirePluralForStack()
    {
        var item = new ClientObject
        {
            Name = "Pyreal Scarab",
            PluralName = "Pyreal Scarabs",
            StackSize = 2,
        };

        Assert.Equal("Pyreal Scarabs", item.GetAppropriateName());
    }

    [Theory]
    [InlineData("Arrow", "Arrows")]
    [InlineData("Pyreal", "Pyreals")]
    [InlineData("Compass", "Compasses")]
    public void AppropriateName_withoutWirePlural_usesRetailSuffixFallback(
        string singular, string expected)
    {
        var item = new ClientObject { Name = singular, StackSize = 2 };

        Assert.Equal(expected, item.GetAppropriateName());
    }

    [Fact]
    public void AppropriateName_forSingleItem_remainsSingular()
    {
        var item = new ClientObject
        {
            Name = "Pyreal Scarab",
            PluralName = "Pyreal Scarabs",
            StackSize = 1,
        };

        Assert.Equal("Pyreal Scarab", item.GetAppropriateName());
    }
}
