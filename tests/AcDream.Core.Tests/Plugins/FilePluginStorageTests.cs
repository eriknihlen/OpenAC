using AcDream.Core.Plugins;
using AcDream.Plugin.Abstractions;

namespace AcDream.Core.Tests.Plugins;

public sealed class FilePluginStorageTests
{
    [Fact]
    public void WriteReadReplaceAndDeleteStayUnderConfiguredRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-storage-{Guid.NewGuid():N}");
        try
        {
            var storage = new FilePluginStorage(root);
            storage.WriteText("plugin/profile.json", "one");
            storage.WriteText("plugin/imports/route.nav", "nav");
            Assert.Equal("one", storage.ReadText("plugin/profile.json"));
            storage.WriteText("plugin/profile.json", "two");
            Assert.Equal("two", storage.ReadText("plugin/profile.json"));
            Assert.Equal(
                ["plugin/imports/route.nav"],
                storage.List("plugin/imports"));
            Assert.True(storage.Delete("plugin/profile.json"));
            Assert.Null(storage.ReadText("plugin/profile.json"));
            Assert.Throws<ArgumentException>(() =>
                storage.WriteText("../escape.json", "bad"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScopedJsonStorageUsesAnAtomicChildNamespace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"acdream-plugin-storage-{Guid.NewGuid():N}");
        try
        {
            var storage = new FilePluginStorage(root).OpenScope(
                PluginStorageScope.Character("Aster"));
            storage.WriteJson("route.json", new Route("holtburg", 3));
            Assert.Equal(new Route("holtburg", 3), storage.ReadJson<Route>("route.json"));
            Assert.EndsWith(Path.Combine("character", "Aster"), storage.RootPath, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Route(string Destination, int Legs);

    [Fact]
    public void TheReportedRootIsTheAbsoluteDirectoryKeysResolveAgainst()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"acdream-plugin-storage-{Guid.NewGuid():N}");
        try
        {
            var storage = new FilePluginStorage(root);
            storage.WriteText("profile.json", "one");

            Assert.Equal(Path.GetFullPath(root), storage.RootPath);
            Assert.True(File.Exists(
                Path.Combine(storage.RootPath!, "profile.json")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
