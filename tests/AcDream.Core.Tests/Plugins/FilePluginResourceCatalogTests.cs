using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

public sealed class FilePluginResourceCatalogTests
{
    [Fact]
    public void PackageResourcesAreReadableAndTraversalIsRejected()
    {
        string root = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "maps"));
            File.WriteAllText(Path.Combine(root, "maps", "dereth.dat"), "map");
            var catalog = new FilePluginResourceCatalog(root);
            using Stream stream = catalog.OpenRead("maps/dereth.dat")!;
            using var reader = new StreamReader(stream);
            Assert.Equal("map", reader.ReadToEnd());
            Assert.Throws<ArgumentException>(() => catalog.OpenRead("../secret"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DataFilesAreUnionedAcrossOverrideLayers()
    {
        string root = CreateDirectory();
        string user = CreateDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "routes"));
            Directory.CreateDirectory(Path.Combine(user, "routes"));
            File.WriteAllText(Path.Combine(root, "routes", "default.json"), "default");
            File.WriteAllText(Path.Combine(user, "routes", "custom.json"), "custom");
            var catalog = new FilePluginResourceCatalog(root, [user]);
            Assert.Equal(["routes/custom.json", "routes/default.json"], catalog.ListDataFiles("routes"));
        }
        finally { Directory.Delete(root, true); Directory.Delete(user, true); }
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"acdream-resources-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
