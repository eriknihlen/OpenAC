using DatReaderWriter.Options;
using AcDream.Content;

namespace AcDream.App.Tests;

public sealed class RuntimeDatCollectionFactoryTests
{
    [Fact]
    public void CreateReadOnlyOptions_BoundsFilePayloadResidencyAndRetainsIndexLookupCache()
    {
        const string datDirectory = @"C:\RetailDats";

        DatCollectionOptions options =
            RuntimeDatCollectionFactory.CreateReadOnlyOptions(datDirectory);

        Assert.Equal(datDirectory, options.DatDirectory);
        Assert.Equal(DatAccessType.Read, options.AccessType);
        Assert.Equal(IndexCachingStrategy.OnDemand, options.IndexCachingStrategy);
        Assert.Equal(FileCachingStrategy.Never, options.FileCachingStrategy);

        Assert.Equal(IndexCachingStrategy.OnDemand, options.PortalIndexCachingStrategy);
        Assert.Equal(IndexCachingStrategy.OnDemand, options.CellIndexCachingStrategy);
        Assert.Equal(IndexCachingStrategy.OnDemand, options.LocalIndexCachingStrategy);
        Assert.Equal(IndexCachingStrategy.OnDemand, options.HighResIndexCachingStrategy);

        Assert.Equal(FileCachingStrategy.Never, options.PortalFileCachingStrategy);
        Assert.Equal(FileCachingStrategy.Never, options.CellFileCachingStrategy);
        Assert.Equal(FileCachingStrategy.Never, options.LocalFileCachingStrategy);
        Assert.Equal(FileCachingStrategy.Never, options.HighResFileCachingStrategy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateReadOnlyOptions_RejectsMissingDatDirectory(string? datDirectory)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            RuntimeDatCollectionFactory.CreateReadOnlyOptions(datDirectory!));
    }
}
