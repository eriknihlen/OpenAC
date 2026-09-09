using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

public class PluginDiscoveryTests : IDisposable
{
    private readonly string _tempDir;

    public PluginDiscoveryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "acdream-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteManifest(string folder, string json)
    {
        var dir = Path.Combine(_tempDir, folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "plugin.json");
        File.WriteAllText(path, json);
        return dir;
    }

    private const string ValidManifest = """
    {
      "id": "acdream.example",
      "displayName": "Example",
      "version": "0.1.0",
      "entryDll": "example.dll",
      "apiVersion": 1
    }
    """;

    [Fact]
    public void Scan_EmptyDirectory_ReturnsEmpty()
    {
        var results = PluginDiscovery.Scan(_tempDir);
        Assert.Empty(results);
    }

    [Fact]
    public void Scan_NonexistentDirectory_ReturnsEmpty()
    {
        var results = PluginDiscovery.Scan(Path.Combine(_tempDir, "does-not-exist"));
        Assert.Empty(results);
    }

    [Fact]
    public void Scan_OneValidPlugin_ReturnsOneSuccess()
    {
        var pluginDir = WriteManifest("example", ValidManifest);

        var results = PluginDiscovery.Scan(_tempDir);

        var single = Assert.Single(results);
        Assert.True(single.Success);
        Assert.NotNull(single.Manifest);
        Assert.Equal("acdream.example", single.Manifest!.Id);
        Assert.Equal(pluginDir, single.PluginDirectory);
    }

    [Fact]
    public void Scan_SubdirWithoutManifest_IsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "noplugin"));
        WriteManifest("real", ValidManifest);

        var results = PluginDiscovery.Scan(_tempDir);

        Assert.Single(results);
    }

    [Fact]
    public void Scan_InvalidManifest_ReturnsFailureButKeepsGoing()
    {
        WriteManifest("broken", "{ not json");
        WriteManifest("ok", ValidManifest);

        var results = PluginDiscovery.Scan(_tempDir).ToList();

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => !r.Success && r.Error != null);
        Assert.Contains(results, r => r.Success);
    }
}
