using AcDream.App.Plugins;

namespace AcDream.App.Tests.Plugins;

public sealed class VtankProfilesDefaultTests
{
    [Fact]
    public void ResolveIsBuiltWithPathCombineOnly()
    {
        const string injectedRoot = "C:/some/injected/data-dir";
        string expected = Path.Combine(injectedRoot, "vtank");

        string actual = VtankProfilesDefault.Resolve(injectedRoot);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain(
            injectedRoot + "\\vtank",
            actual.Replace(Path.DirectorySeparatorChar, '/'),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveAppendsVtankBeneathWhicheverRootIsInjected()
    {
        Assert.Equal(
            Path.Combine("/home/user/.local/share/acdream", "vtank"),
            VtankProfilesDefault.Resolve("/home/user/.local/share/acdream"));
    }
}
