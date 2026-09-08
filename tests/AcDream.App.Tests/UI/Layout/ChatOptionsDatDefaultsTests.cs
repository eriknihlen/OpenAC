using AcDream.App.UI.Layout;
using DatReaderWriter;
using DatReaderWriter.Options;
using SysEnv = System.Environment;

namespace AcDream.App.Tests.UI.Layout;

public sealed class ChatOptionsDatDefaultsTests
{
    [Fact]
    [Trait("Lane", "InstalledDat")]
    public void InstalledDat_ResolvesTheByteVerifiedSliderDefaults()
    {
        string datDir = ResolveDatDir();
        using var dats = new DatCollection(datDir, DatAccessType.Read);

        bool resolved = ChatOptionsDatDefaults.TryRead(
            dats, out float defaultOpacity, out float activeOpacity);

        Assert.True(resolved);
        Assert.Equal(0.5f, defaultOpacity);
        Assert.Equal(1.0f, activeOpacity);
    }

    private static string ResolveDatDir()
    {
        string? configured = SysEnv.GetEnvironmentVariable("ACDREAM_DAT_DIR");
        if (!string.IsNullOrWhiteSpace(configured)
            && File.Exists(Path.Combine(configured, "client_portal.dat")))
            return configured;

        const string installed = @"C:\Turbine\Asheron's Call";
        if (File.Exists(Path.Combine(installed, "client_portal.dat")))
            return installed;

        string conventional = Path.Combine(
            SysEnv.GetFolderPath(SysEnv.SpecialFolder.UserProfile),
            "Documents",
            "Asheron's Call");
        if (File.Exists(Path.Combine(conventional, "client_portal.dat")))
            return conventional;

        throw new InvalidOperationException(
            "Lane=InstalledDat requires client_portal.dat; see docs/release-gate.md.");
    }
}
