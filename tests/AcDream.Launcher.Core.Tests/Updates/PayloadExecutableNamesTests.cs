using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class PayloadExecutableNamesTests
{
    [Theory]
    [InlineData("win-x64", "AcDream.App")]
    [InlineData("linux-x64", "AcDream.App")]
    [InlineData("osx-arm64", "acdream-client")]
    public void GraphicalHostSelectionKeepsExistingPlatformsAndRenamesMac(string rid, string expected)
    {
        Assert.Equal(expected, PayloadExecutableNames.GraphicalHostForRid(rid));
        Assert.Contains(
            expected + PayloadExecutableNames.SuffixForRid(rid),
            PayloadExecutableNames.RequiredForPayload(rid, launcherPayload: false));
    }
}
