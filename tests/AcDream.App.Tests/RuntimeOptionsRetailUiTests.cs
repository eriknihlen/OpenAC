using System.Collections.Generic;
using AcDream.App;

namespace AcDream.App.Tests;

public class RuntimeOptionsRetailUiTests
{
    [Fact]
    public void Parse_RetailUiIsDefaultOnAndReadsAcDir()
    {
        var env = new Dictionary<string, string?>
        {
            ["ACDREAM_AC_DIR"] = @"C:\Turbine\Asheron's Call",
        };
        var opts = RuntimeOptions.Parse("dats", k => env.GetValueOrDefault(k));
        Assert.True(opts.RetailUi);
        Assert.Equal(@"C:\Turbine\Asheron's Call", opts.AcDir);
    }

    [Fact]
    public void Parse_DefaultsRetailUiOnAndAcDirNull()
    {
        var opts = RuntimeOptions.Parse("dats", _ => null);
        Assert.True(opts.RetailUi);
        Assert.Null(opts.AcDir);
    }

    [Fact]
    public void Parse_LiteralZeroOptsOutOfRetailUi()
    {
        var opts = RuntimeOptions.Parse(
            "dats",
            key => key == "ACDREAM_RETAIL_UI" ? "0" : null);

        Assert.False(opts.RetailUi);
    }

    [Fact]
    public void Parse_ReadsUiProbeOptions()
    {
        var env = new Dictionary<string, string?>
        {
            ["ACDREAM_UI_PROBE_DUMP"] = "1",
            ["ACDREAM_UI_PROBE_SCRIPT"] = @"C:\tmp\ui-probe.txt",
            ["ACDREAM_AUTOMATION_ARTIFACT_DIR"] = @"C:\tmp\world-gate",
        };

        var opts = RuntimeOptions.Parse("dats", k => env.GetValueOrDefault(k));

        Assert.True(opts.UiProbeDump);
        Assert.Equal(@"C:\tmp\ui-probe.txt", opts.UiProbeScript);
        Assert.Equal(@"C:\tmp\world-gate", opts.AutomationArtifactDirectory);
        Assert.True(opts.UiProbeEnabled);
    }
}
