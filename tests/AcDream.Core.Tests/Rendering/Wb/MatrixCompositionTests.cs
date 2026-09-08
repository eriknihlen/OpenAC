using System.Numerics;
using AcDream.App.Rendering.Wb;

namespace AcDream.Core.Tests.Rendering.Wb;

public sealed class MatrixCompositionTests
{
    [Fact]
    public void Compose_EntityAnimRest_ProducesExpectedWorldMatrix()
    {
        var entityWorld = Matrix4x4.CreateTranslation(100, 200, 300);
        var animOverride = Matrix4x4.CreateRotationZ(MathF.PI / 4);
        var restPose = Matrix4x4.CreateTranslation(1, 0, 0);

        var result = WbDrawDispatcher.ComposePartWorldMatrix(entityWorld, animOverride, restPose);

        var expected = restPose * animOverride * entityWorld;
        AssertMatrixEqual(expected, result);
    }

    [Fact]
    public void Compose_IdentityAnim_EqualsRestTimesEntity()
    {
        var entityWorld = Matrix4x4.CreateFromQuaternion(
            Quaternion.CreateFromYawPitchRoll(0.5f, 0, 0)) *
            Matrix4x4.CreateTranslation(10, 20, 30);
        var restPose = Matrix4x4.CreateTranslation(0.5f, -0.3f, 0.1f);

        var result = WbDrawDispatcher.ComposePartWorldMatrix(
            entityWorld, Matrix4x4.Identity, restPose);

        var expected = restPose * entityWorld;
        AssertMatrixEqual(expected, result);
    }

    [Fact]
    public void Compose_AllIdentity_ReturnsIdentity()
    {
        var result = WbDrawDispatcher.ComposePartWorldMatrix(
            Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity);

        AssertMatrixEqual(Matrix4x4.Identity, result);
    }

    private static void AssertMatrixEqual(Matrix4x4 expected, Matrix4x4 actual, float eps = 1e-5f)
    {
        Assert.Equal(expected.M11, actual.M11, eps);
        Assert.Equal(expected.M12, actual.M12, eps);
        Assert.Equal(expected.M13, actual.M13, eps);
        Assert.Equal(expected.M14, actual.M14, eps);
        Assert.Equal(expected.M21, actual.M21, eps);
        Assert.Equal(expected.M22, actual.M22, eps);
        Assert.Equal(expected.M23, actual.M23, eps);
        Assert.Equal(expected.M24, actual.M24, eps);
        Assert.Equal(expected.M31, actual.M31, eps);
        Assert.Equal(expected.M32, actual.M32, eps);
        Assert.Equal(expected.M33, actual.M33, eps);
        Assert.Equal(expected.M34, actual.M34, eps);
        Assert.Equal(expected.M41, actual.M41, eps);
        Assert.Equal(expected.M42, actual.M42, eps);
        Assert.Equal(expected.M43, actual.M43, eps);
        Assert.Equal(expected.M44, actual.M44, eps);
    }
}
