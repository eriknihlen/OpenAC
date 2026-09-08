using System;
using System.Numerics;
using AcDream.Core.Vfx;
using Xunit;

namespace AcDream.Core.Tests.Vfx;

public sealed class RetailParticleFacingTests
{
    private const float Eps = 1e-4f;

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.True(
            Vector3.Distance(expected, actual) < 1e-3f,
            $"expected {expected}, got {actual}");
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, false)]
    [InlineData(2u, true)]
    [InlineData(3u, true)]
    [InlineData(4u, true)]
    [InlineData(5u, true)]
    [InlineData(6u, false)]
    public void Faces_MatchesRetailModeWindow(uint mode, bool expected)
        => Assert.Equal(expected, RetailParticleFacing.Faces(mode));

    [Fact]
    public void Mode2_ViewerNorth_QuadXStaysEastAndYIsWorldUp()
    {
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            2u,
            Quaternion.Identity,
            Vector3.UnitX,
            Vector3.UnitY,
            toViewerUnit: Vector3.UnitY,
            fallbackRight: Vector3.UnitX,
            fallbackUp: Vector3.UnitZ);

        AssertVector(Vector3.UnitX, xd);
        AssertVector(Vector3.UnitZ, yd);
    }

    [Fact]
    public void Mode2_QuadPlaneIsPerpendicularToViewerWithNormalTowardThem()
    {
        Vector3 toViewer = Vector3.Normalize(new Vector3(0.4f, -0.7f, 0.59f));
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            2u,
            Quaternion.Identity,
            Vector3.UnitX,
            Vector3.UnitY,
            toViewer,
            Vector3.UnitX,
            Vector3.UnitZ);

        Assert.True(MathF.Abs(Vector3.Dot(xd, toViewer)) < Eps);
        Assert.True(MathF.Abs(Vector3.Dot(yd, toViewer)) < Eps);
        // Roll-free: the X span stays horizontal.
        Assert.True(MathF.Abs(xd.Z) < Eps);
        Assert.True(MathF.Abs(Vector3.Dot(Vector3.Cross(xd, yd), toViewer)) > 0.99f);
    }

    [Fact]
    public void Mode2_ViewerStraightOverhead_FallsBackToCameraPlane()
    {
        var fallbackRight = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        var fallbackUp = Vector3.UnitZ;
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            2u,
            Quaternion.Identity,
            Vector3.UnitX,
            Vector3.UnitY,
            toViewerUnit: Vector3.UnitZ,
            fallbackRight,
            fallbackUp);

        AssertVector(fallbackRight, xd);
        AssertVector(fallbackUp, yd);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(7u)]
    public void NonFacingModes_KeepTheAuthoredOrientation(uint mode)
    {
        var orientation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, MathF.PI / 2f);
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            mode,
            orientation,
            Vector3.UnitX,
            Vector3.UnitZ,
            toViewerUnit: Vector3.UnitY,
            Vector3.UnitX,
            Vector3.UnitZ);

        AssertVector(Vector3.UnitY, xd);   // +X yawed 90° -> +Y
        AssertVector(Vector3.UnitZ, yd);   // spin axis unchanged
    }

    [Fact]
    public void Mode5_SpinsAroundLocalZUntilTheNormalFacesTheViewer()
    {
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            5u,
            Quaternion.Identity,
            Vector3.UnitX,
            Vector3.UnitZ,
            toViewerUnit: Vector3.UnitX,
            Vector3.UnitX,
            Vector3.UnitZ);

        AssertVector(Vector3.UnitZ, yd);
        AssertVector(Vector3.UnitX, Vector3.Cross(xd, yd));
    }

    [Fact]
    public void Mode4_ViewerAlongTheConstrainedAxis_KeepsAuthoredOrientation()
    {
        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            4u,
            Quaternion.Identity,
            Vector3.UnitX,
            Vector3.UnitZ,
            toViewerUnit: Vector3.UnitY,
            Vector3.UnitX,
            Vector3.UnitZ);

        AssertVector(Vector3.UnitX, xd);
        AssertVector(Vector3.UnitZ, yd);
    }

    [Fact]
    public void Mode3_HonorsTheParticleOrientationWhenSpinning()
    {
        var orientation = Quaternion.CreateFromAxisAngle(
            Vector3.UnitZ, MathF.PI / 2f);
        Vector3 axisWorld = Vector3.Transform(Vector3.UnitX, orientation);
        Vector3 toViewer = Vector3.Normalize(new Vector3(0.3f, 0.1f, 0.95f));

        (Vector3 xd, Vector3 yd) = RetailParticleFacing.OrientQuad(
            3u,
            orientation,
            Vector3.UnitX,
            Vector3.UnitZ,
            toViewer,
            Vector3.UnitX,
            Vector3.UnitZ);

        Vector3 normal = Vector3.Normalize(Vector3.Cross(xd, yd));
        Vector3 targetInPlane = Vector3.Normalize(
            toViewer - axisWorld * Vector3.Dot(toViewer, axisWorld));
        Assert.True(MathF.Abs(Vector3.Dot(normal, axisWorld)) < 1e-3f);
        Assert.True(Vector3.Dot(normal, targetInPlane) > 0.999f);
    }
}
