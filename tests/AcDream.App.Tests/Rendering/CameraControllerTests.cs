using AcDream.App.Rendering;
using AcDream.Core.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

[Collection(CameraDiagnosticsCollection.Name)]
public class CameraControllerTests
{
    private static (CameraController ctl, ChaseCamera legacy, RetailChaseCamera retail) MakeChaseFixture()
    {
        var orbit  = new OrbitCamera();
        var fly    = new FlyCamera();
        var ctl    = new CameraController(orbit, fly);
        var legacy = new ChaseCamera();
        var retail = new RetailChaseCamera();
        ctl.EnterChaseMode(legacy, retail);
        return (ctl, legacy, retail);
    }

    [Fact]
    public void ChaseMode_WhenFlagOff_ActiveIsLegacy()
    {
        bool saved = CameraDiagnostics.UseRetailChaseCamera;
        try
        {
            CameraDiagnostics.UseRetailChaseCamera = false;
            var (ctl, legacy, _) = MakeChaseFixture();
            Assert.Same(legacy, ctl.Active);
            Assert.True(ctl.IsChaseMode);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = saved;
        }
    }

    [Fact]
    public void ChaseMode_WhenFlagOn_ActiveIsRetail()
    {
        bool saved = CameraDiagnostics.UseRetailChaseCamera;
        try
        {
            CameraDiagnostics.UseRetailChaseCamera = true;
            var (ctl, _, retail) = MakeChaseFixture();
            Assert.Same(retail, ctl.Active);
            Assert.True(ctl.IsChaseMode);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = saved;
        }
    }

    [Fact]
    public void ChaseMode_FlagFlipped_ActiveSwaps()
    {
        bool saved = CameraDiagnostics.UseRetailChaseCamera;
        try
        {
            CameraDiagnostics.UseRetailChaseCamera = false;
            var (ctl, legacy, retail) = MakeChaseFixture();
            Assert.Same(legacy, ctl.Active);

            CameraDiagnostics.UseRetailChaseCamera = true;
            Assert.Same(retail, ctl.Active);

            CameraDiagnostics.UseRetailChaseCamera = false;
            Assert.Same(legacy, ctl.Active);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = saved;
        }
    }

    [Fact]
    public void ExitChaseMode_ClearsBothCameras()
    {
        bool saved = CameraDiagnostics.UseRetailChaseCamera;
        try
        {
            CameraDiagnostics.UseRetailChaseCamera = false;
            var (ctl, _, _) = MakeChaseFixture();
            ctl.ExitChaseMode();

            Assert.Null(ctl.Chase);
            Assert.Null(ctl.RetailChase);
            Assert.False(ctl.IsChaseMode);
        }
        finally
        {
            CameraDiagnostics.UseRetailChaseCamera = saved;
        }
    }

    [Fact]
    public void ExitChaseMode_AlreadyOfflinePreservesOrbitCamera()
    {
        var orbit = new OrbitCamera();
        var ctl = new CameraController(orbit, new FlyCamera());

        ctl.ExitChaseMode();

        Assert.Same(orbit, ctl.Active);
        Assert.False(ctl.IsFlyMode);
        Assert.False(ctl.IsChaseMode);
    }

    [Fact]
    public void ExitChaseMode_AfterChaseToFlyReleasesRetainedSessionCameras()
    {
        var (ctl, _, _) = MakeChaseFixture();
        ctl.ToggleFly();
        Assert.True(ctl.IsFlyMode);
        Assert.NotNull(ctl.Chase);
        Assert.NotNull(ctl.RetailChase);

        ctl.ExitChaseMode();

        Assert.False(ctl.IsFlyMode);
        Assert.False(ctl.IsChaseMode);
        Assert.Null(ctl.Chase);
        Assert.Null(ctl.RetailChase);
    }

    [Fact]
    public void ExitChaseMode_FromChaseLandsOnOrbitAndNotifies()
    {
        var (ctl, _, _) = MakeChaseFixture();
        int notifications = 0;
        bool? lastArg = null;
        ctl.ModeChanged += arg => { notifications++; lastArg = arg; };

        ctl.ExitChaseMode();

        Assert.False(ctl.IsChaseMode);
        Assert.False(ctl.IsFlyMode);
        Assert.Equal(1, notifications);
        Assert.False(lastArg);
    }

    [Fact]
    public void ExitChaseMode_FromFlyNotifiesSoCursorCaptureReleases()
    {
        var ctl = new CameraController(new OrbitCamera(), new FlyCamera());
        ctl.ToggleFly();
        Assert.True(ctl.IsFlyMode);
        int notifications = 0;
        ctl.ModeChanged += _ => notifications++;

        ctl.ExitChaseMode();

        Assert.False(ctl.IsFlyMode);
        Assert.Equal(1, notifications);
    }

    [Fact]
    public void ExitChaseMode_FromOrbitDoesNotNotify()
    {
        var ctl = new CameraController(new OrbitCamera(), new FlyCamera());
        int notifications = 0;
        ctl.ModeChanged += _ => notifications++;

        ctl.ExitChaseMode();

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void RestoreState_ReestablishesPriorCameraAfterNotificationFailure()
    {
        var orbit = new OrbitCamera();
        var ctl = new CameraController(orbit, new FlyCamera());
        CameraController.CameraState prior = ctl.CaptureState();
        int notifications = 0;
        ctl.ModeChanged += _ =>
        {
            if (++notifications == 1)
                throw new InvalidOperationException("injected camera observer failure");
        };

        Assert.Throws<InvalidOperationException>(() =>
            ctl.EnterChaseMode(new ChaseCamera(), new RetailChaseCamera()));

        ctl.RestoreState(prior);

        Assert.Same(orbit, ctl.Active);
        Assert.False(ctl.IsFlyMode);
        Assert.False(ctl.IsChaseMode);
        Assert.Null(ctl.Chase);
        Assert.Null(ctl.RetailChase);
        Assert.Equal(2, notifications);
    }
}
