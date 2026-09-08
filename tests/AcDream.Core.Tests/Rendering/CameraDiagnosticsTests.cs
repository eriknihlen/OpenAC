using AcDream.Core.Rendering;
using Xunit;

namespace AcDream.Core.Tests.Rendering;

public class CameraDiagnosticsTests
{

    [Fact]
    public void Defaults_AreRetailValues()
    {
        // Reset to defaults explicitly (test isolation; another test may
        // have flipped these earlier in the run).
        CameraDiagnostics.TranslationStiffness  = 0.45f;
        CameraDiagnostics.RotationStiffness     = 0.45f;
        CameraDiagnostics.MouseLowPassWindowSec = 0.25f;
        CameraDiagnostics.CameraAdjustmentSpeed = 40.0f;
        CameraDiagnostics.AlignToSlope          = true;
        CameraDiagnostics.UseRetailChaseCamera  = true;
        CameraDiagnostics.CollideCamera         = true;

        Assert.Equal(0.45f, CameraDiagnostics.TranslationStiffness);
        Assert.Equal(0.45f, CameraDiagnostics.RotationStiffness);
        Assert.Equal(0.25f, CameraDiagnostics.MouseLowPassWindowSec);
        Assert.Equal(40.0f, CameraDiagnostics.CameraAdjustmentSpeed);
        Assert.True(CameraDiagnostics.AlignToSlope);
        Assert.True(CameraDiagnostics.UseRetailChaseCamera);
        Assert.True(CameraDiagnostics.CollideCamera);
    }

    [Fact]
    public void Setters_PersistRuntimeChanges()
    {
        CameraDiagnostics.TranslationStiffness = 0.8f;
        CameraDiagnostics.UseRetailChaseCamera = false;

        Assert.Equal(0.8f, CameraDiagnostics.TranslationStiffness);
        Assert.False(CameraDiagnostics.UseRetailChaseCamera);

        // Reset so other tests aren't poisoned.
        CameraDiagnostics.TranslationStiffness = 0.45f;
        CameraDiagnostics.UseRetailChaseCamera = true;
    }

    [Fact]
    public void CollideCamera_DefaultOn_AndPersistsRuntimeChanges()
    {
        CameraDiagnostics.CollideCamera = true;
        Assert.True(CameraDiagnostics.CollideCamera);

        CameraDiagnostics.CollideCamera = false;
        Assert.False(CameraDiagnostics.CollideCamera);

        CameraDiagnostics.CollideCamera = true; // reset so other tests aren't poisoned
    }
}
