using AcDream.Core.Plugins;

namespace AcDream.Core.Tests.Plugins;

public class PluginHostVersionTests
{
    [Theory]
    [InlineData("0.1.7+3a71d75", 0, 1, 7)]
    [InlineData("0.2.0-beta.1+x", 0, 2, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    public void FromInformationalVersion_StripsBuildAndPrerelease(
        string informationalVersion, int major, int minor, int patch)
    {
        PluginHostVersion? version =
            PluginHostVersion.FromInformationalVersion(informationalVersion);

        Assert.Equal(new PluginHostVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData(null)]
    public void FromInformationalVersion_MalformedCore_ReturnsNull(string? informationalVersion)
    {
        Assert.Null(PluginHostVersion.FromInformationalVersion(informationalVersion));
    }
}
