using AcDream.Launcher.Core.Plugins;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Plugins;

public sealed class PluginContentPolicyTests
{
    [Fact]
    public void AcceptsAWellFormedTree()
    {
        PluginContentPolicy.Validate(
            [
                File("plugin.json"),
                File("Hello.dll"),
                File("Hello.pdb"),
                File("assets/icon.png"),
            ],
            "Hello.dll");
    }

    [Theory]
    [InlineData("payload.exe")]
    [InlineData("lib.dylib")]
    [InlineData("install.sh")]
    [InlineData("hook.js")]
    [InlineData("shortcut.lnk")]
    [InlineData("Makefile")]
    public void RejectsADisallowedExtension(string path)
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("plugin.json"), File("Hello.dll"), File(path)],
            "Hello.dll"));
    }

    [Fact]
    public void RejectsARuntimesDirectory()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("plugin.json"), File("Hello.dll"), File("runtimes/win-x64/native.dll")],
            "Hello.dll"));
    }

    [Fact]
    public void RejectsAMissingEntryDll()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("plugin.json")],
            "Hello.dll"));
    }

    [Fact]
    public void RejectsAPluginJsonNotAtRoot()
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("nested/plugin.json"), File("Hello.dll")],
            "Hello.dll"));
    }

    [Theory]
    [InlineData("ICON.PNG")]
    [InlineData("Icon.png")]
    public void RejectsACaseVariantOfTheRootIcon(string path)
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("plugin.json"), File("Hello.dll"), File(path)],
            "Hello.dll"));
    }

    [Theory]
    [InlineData("icon.jpg")]
    [InlineData("icon.jpeg")]
    [InlineData("Icon.JPG")]
    public void RejectsARootJpegIcon(string path)
    {
        Assert.Throws<LauncherUpdateException>(() => PluginContentPolicy.Validate(
            [File("plugin.json"), File("Hello.dll"), File(path)],
            "Hello.dll"));
    }

    [Fact]
    public void AllowsAJpegIconInASubfolder()
    {
        PluginContentPolicy.Validate(
            [
                File("plugin.json"),
                File("Hello.dll"),
                File("assets/icon.jpg"),
            ],
            "Hello.dll");
    }

    private static ExtractedFileRecord File(string path) => new(path, new string('a', 64), 1, 0x1A4);
}
