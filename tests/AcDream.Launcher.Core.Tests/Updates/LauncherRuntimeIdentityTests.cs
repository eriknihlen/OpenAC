using System.Runtime.InteropServices;
using AcDream.Launcher.Core.Updates;

namespace AcDream.Launcher.Core.Tests.Updates;

public sealed class LauncherRuntimeIdentityTests
{
    [Fact]
    public void MacAppleSiliconUsesOsxArm64()
    {
        string rid = LauncherRuntimeIdentity.DetectRid(
            isWindows: false,
            isLinux: false,
            isMacOS: true,
            Architecture.Arm64);

        Assert.Equal("osx-arm64", rid);
    }
}
