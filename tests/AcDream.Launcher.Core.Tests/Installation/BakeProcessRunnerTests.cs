using AcDream.Launcher.Core.Installation;
using AcDream.Platform;

namespace AcDream.Launcher.Core.Tests.Installation;

public sealed class BakeProcessRunnerTests
{
    [Fact]
    public void PublicationNonceIsEnvironmentOnlyAndVisibleArgumentsStayPinned()
    {
        string nonce = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")
            .ToString("N");
        var request = new BakeProcessRequest(
            "acdream-bake",
            "retail-dats",
            "data/pak/acdream.pak",
            7,
            nonce);

        System.Diagnostics.ProcessStartInfo startInfo =
            SystemBakeProcessRunner.CreateStartInfo(request);

        Assert.Equal(request.Arguments, startInfo.ArgumentList);
        Assert.DoesNotContain(nonce, startInfo.ArgumentList);
        Assert.Equal(
            nonce,
            startInfo.Environment[
                BakePublicationGuardPaths.NonceEnvironmentVariable]);
    }

    [Fact]
    public void UnguardedRequestExplicitlyRemovesInheritedAuthorization()
    {
        var request = new BakeProcessRequest(
            "acdream-bake",
            "retail-dats",
            "data/pak/acdream.pak",
            1);

        System.Diagnostics.ProcessStartInfo startInfo =
            SystemBakeProcessRunner.CreateStartInfo(request);

        Assert.False(startInfo.Environment.ContainsKey(
            BakePublicationGuardPaths.NonceEnvironmentVariable));
    }

    [Fact]
    public void FilteredOverlayArgumentsUseBakeToolsPinnedHexLists()
    {
        var request = new BakeProcessRequest(
            "acdream-bake",
            "retail-dats",
            "data/pak/update.pak",
            4,
            DatIds: [0x01000001u, 0x02000002u],
            Landblocks: [0x0A, 0xFE]);

        Assert.Equal(
            [
                "--dat-dir", "retail-dats",
                "--out", "data/pak/update.pak",
                "--threads", "4",
                "--progress-json",
                "--ids", "0x01000001,0x02000002",
                "--landblocks", "0x0A,0xFE",
            ],
            request.Arguments);
    }
}
