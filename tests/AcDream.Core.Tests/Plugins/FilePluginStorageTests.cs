using AcDream.Core.Plugins;

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
