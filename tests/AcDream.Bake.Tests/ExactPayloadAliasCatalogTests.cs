namespace AcDream.Bake.Tests;

public sealed class ExactPayloadAliasCatalogTests
{
    [Fact]
    public void AliasRequiresCompleteByteEquality()
    {
        var catalog = new ExactPayloadAliasCatalog();
        byte[] primary = [1, 2, 3, 4, 5];
        catalog.Add(0x1234UL, primary);

        Assert.True(catalog.TryFind(
            [1, 2, 3, 4, 5],
            out ulong primaryKey));
        Assert.Equal(0x1234UL, primaryKey);

        Assert.False(catalog.TryFind(
            [1, 2, 3, 4, 4],
            out _));
        Assert.False(catalog.TryFind(
            [1, 2, 3, 4, 5, 0],
            out _));
    }
}
