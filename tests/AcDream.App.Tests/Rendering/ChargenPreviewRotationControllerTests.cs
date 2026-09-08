using System.Numerics;
using AcDream.App.Rendering;
using Xunit;

namespace AcDream.App.Tests.Rendering;

public sealed class ChargenPreviewRotationControllerTests
{
    [Fact]
    public void DefaultConstructor_StartsAtRetailsOperative180DegreeHeading()
    {
        var controller = new ChargenPreviewRotationController();

        Assert.Equal(180f, controller.HeadingDegrees);
    }

    [Fact]
    public void Toggle_StartsRotatingInTheGivenDirection()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);

        Assert.True(controller.IsRotating);
        Assert.Equal(ChargenRotateDirection.Clockwise, controller.Direction);
    }

    [Fact]
    public void Toggle_SameDirectionWhileRotating_Stops()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Toggle(ChargenRotateDirection.Clockwise);

        Assert.False(controller.IsRotating);
    }

    [Fact]
    public void Toggle_OppositeDirectionWhileRotating_SwitchesDirectionAndKeepsRotating()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Toggle(ChargenRotateDirection.CounterClockwise);

        Assert.True(controller.IsRotating);
        Assert.Equal(ChargenRotateDirection.CounterClockwise, controller.Direction);
    }

    [Fact]
    public void Tick_WhileNotRotating_IsANoOp()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Tick(100.0);

        Assert.Equal(0f, controller.HeadingDegrees);
    }

    [Fact]
    public void Tick_FirstCallAfterToggle_ContributesZeroDelta()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Tick(1000.0);

        Assert.Equal(0f, controller.HeadingDegrees);
    }

    [Fact]
    public void Tick_ClockwiseAdvance_AddsTheExactPerTickFormula()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Tick(10.0);  // seeds lastRotateTime = 10, zero delta.
        controller.Tick(11.5);  // half a revolution at 3 s/rev.

        Assert.Equal(180f, controller.HeadingDegrees, 3);
    }

    [Fact]
    public void Tick_CounterClockwiseAdvance_SubtractsAndWrapsPositive()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.CounterClockwise);
        controller.Tick(10.0);
        controller.Tick(11.5); // would go to -180, wraps to +180.

        Assert.Equal(180f, controller.HeadingDegrees, 3);
    }

    [Fact]
    public void Tick_AccumulatesAcrossMultipleTicks()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Tick(10.0);
        controller.Tick(10.5); // +60 deg.
        controller.Tick(11.0); // +60 deg more.

        Assert.Equal(120f, controller.HeadingDegrees, 3);
    }

    [Fact]
    public void Tick_ClockwiseAdvancePast360_ClampsBackBySubtracting360()
    {
        var controller = new ChargenPreviewRotationController(0f);
        controller.Toggle(ChargenRotateDirection.Clockwise);
        controller.Tick(10.0);       // seeds lastRotateTime = 10, zero delta.
        controller.Tick(10.0 + 3.5);

        Assert.Equal(60f, controller.HeadingDegrees, 3);
    }

    [Fact]
    public void ToOrientation_AtZeroHeading_IsIdentity()
    {
        var controller = new ChargenPreviewRotationController(0f);
        Quaternion orientation = controller.ToOrientation();

        Assert.Equal(Quaternion.Identity.X, orientation.X, 4);
        Assert.Equal(Quaternion.Identity.Y, orientation.Y, 4);
        Assert.Equal(Quaternion.Identity.Z, orientation.Z, 4);
        Assert.Equal(Quaternion.Identity.W, orientation.W, 4);
    }
}
