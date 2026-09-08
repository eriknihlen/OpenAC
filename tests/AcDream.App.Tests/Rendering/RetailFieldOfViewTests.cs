using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class RetailFieldOfViewTests
{
    [Theory]
    // 4:3 CRT: 90° / (1.3333 − 0.1) = 1.27362 rad ≈ 72.97° vertical.
    [InlineData(4f / 3f, 1.27362f)]
    // 16:9: 90° / (1.7778 − 0.1) = 0.93624 rad ≈ 53.64° vertical.
    [InlineData(16f / 9f, 0.93624f)]
    // 21:9 ultrawide: 90° / (2.3333 − 0.1) = 0.70327 rad ≈ 40.29° vertical.
    [InlineData(21f / 9f, 0.70327f)]
    public void Law_AtTheDefault90DegreeGameFov_MatchesTheReferenceFormula(
        float aspect, float expectedFovY)
    {
        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            RetailFieldOfView.DefaultGameFovRadians, aspect, out float fovY));
        Assert.Equal(expectedFovY, fovY, precision: 4);
    }

    [Fact]
    public void Law_HoldsTheHorizontalViewRoughlyConstant()
    {
        foreach (float aspect in new[] { 4f / 3f, 16f / 9f, 21f / 9f })
        {
            Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
                RetailFieldOfView.DefaultGameFovRadians, aspect, out float fovY));
            float horizontal = 2f * MathF.Atan(MathF.Tan(fovY / 2f) * aspect);
            Assert.InRange(horizontal, 80f * MathF.PI / 180f, 90f * MathF.PI / 180f);
        }
    }

    [Theory]
    // Degenerate aspects at or below the 0.1 bias: divisor ≤ 0.
    [InlineData(0.05f)]
    [InlineData(0.1f)]
    [InlineData(0.55f)]
    public void Gate_RejectsResultsOutsideRetailsAcceptedInterval(float aspect)
    {
        Assert.False(RetailFieldOfView.TryAppliedVerticalFov(
            RetailFieldOfView.DefaultGameFovRadians, aspect, out _));
    }

    [Fact]
    public void DefaultAppliedFovY_IsTheLawAtTheDefaultPair()
    {
        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            RetailFieldOfView.DefaultGameFovRadians, 16f / 9f, out float expected));
        Assert.Equal(expected, RetailFieldOfView.DefaultAppliedFovY);
    }

    [Fact]
    public void Controller_SetAspect_DrivesEveryAttachedCamera_IncludingChase()
    {
        var controller = new CameraController(new OrbitCamera(), new FlyCamera());
        var chase = new ChaseCamera();
        var retailChase = new RetailChaseCamera();
        controller.EnterChaseMode(chase, retailChase);

        controller.SetAspect(4f / 3f);

        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            controller.GameFovRadians, 4f / 3f, out float expectedFov));
        foreach ((float aspect, float fov) in new[]
        {
            (controller.Orbit.Aspect, controller.Orbit.FovY),
            (controller.Fly.Aspect, controller.Fly.FovY),
            (chase.Aspect, chase.FovY),
            (retailChase.Aspect, retailChase.FovY),
        })
        {
            Assert.Equal(4f / 3f, aspect);
            Assert.Equal(expectedFov, fov, precision: 5);
        }
    }

    [Fact]
    public void Controller_EnterChaseMode_ConvergesFreshCamerasImmediately()
    {
        var controller = new CameraController(new OrbitCamera(), new FlyCamera());
        controller.SetAspect(21f / 9f);

        var chase = new ChaseCamera();
        var retailChase = new RetailChaseCamera();
        controller.EnterChaseMode(chase, retailChase);

        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            controller.GameFovRadians, 21f / 9f, out float expectedFov));
        Assert.Equal(21f / 9f, chase.Aspect);
        Assert.Equal(expectedFov, chase.FovY, precision: 5);
        Assert.Equal(21f / 9f, retailChase.Aspect);
        Assert.Equal(expectedFov, retailChase.FovY, precision: 5);
    }

    [Fact]
    public void Controller_RejectedLaw_KeepsThePreviousFovButPropagatesAspect()
    {
        var controller = new CameraController(new OrbitCamera(), new FlyCamera());
        float before = controller.Orbit.FovY;

        controller.SetAspect(0.5f);   // 90°/(0.5−0.1) = 3.93 rad > π → rejected

        Assert.Equal(0.5f, controller.Orbit.Aspect);
        Assert.Equal(before, controller.Orbit.FovY);
    }

    [Fact]
    public void Controller_SetGameFov_RecomputesAtTheCurrentAspect()
    {
        var controller = new CameraController(new OrbitCamera(), new FlyCamera());
        controller.SetAspect(16f / 9f);

        float narrow = 45f * MathF.PI / 180f;   // slider dragged to 45°
        controller.SetGameFov(narrow);

        Assert.True(RetailFieldOfView.TryAppliedVerticalFov(
            narrow, 16f / 9f, out float expectedFov));
        Assert.Equal(narrow, controller.GameFovRadians);
        Assert.Equal(expectedFov, controller.Fly.FovY, precision: 5);
    }
}
