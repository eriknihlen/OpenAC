using System.Numerics;
using AcDream.App.Rendering;

namespace AcDream.App.Tests.Rendering;

public sealed class DirectionalShadowCascadeFitterTests
{
    [Theory]
    [InlineData(DirectionalShadowPreset.Low, 2, 72f)]
    [InlineData(DirectionalShadowPreset.Medium, 3, 144f)]
    [InlineData(DirectionalShadowPreset.High, 4, 240f)]
    internal void Fit_UsesPracticalIncreasingSplitsAndExactPresetReach(
        DirectionalShadowPreset preset,
        int expectedCount,
        float expectedReach)
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(preset);
        DirectionalShadowCascadeFitInput input = CameraInput(
            Vector3.Zero,
            quality);
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[4];

        int count = DirectionalShadowCascadeFitter.Fit(in input, cascades);

        Assert.Equal(expectedCount, count);
        float previous = input.CameraNearMeters;
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(previous, cascades[i].SplitNearMeters);
            Assert.True(cascades[i].SplitFarMeters > previous);
            Assert.True(cascades[i].TexelWorldSize > 0f);
            Assert.True(float.IsFinite(cascades[i].WorldToShadowClip.M11));
            previous = cascades[i].SplitFarMeters;
        }
        Assert.Equal(expectedReach, cascades[count - 1].SplitFarMeters, 3);
    }

    [Fact]
    public void TexelStabilization_SubTexelCameraTranslationKeepsSnappedCenter()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.Medium);
        DirectionalShadowCascadeFitInput firstInput = CameraInput(
            new Vector3(100f, 200f, 30f),
            quality);
        Span<DirectionalShadowCascade> first =
            stackalloc DirectionalShadowCascade[4];
        DirectionalShadowCascadeFitter.Fit(in firstInput, first);

        Vector3 light = Vector3.Normalize(firstInput.SurfaceToLightDirection);
        Vector3 lightX = Vector3.Normalize(Vector3.Cross(
            DirectionalShadowCascadeFitter.StableLightUp(light),
            light));
        Vector3 movement = lightX * (first[0].TexelWorldSize * 0.2f);
        DirectionalShadowCascadeFitInput secondInput = CameraInput(
            new Vector3(100f, 200f, 30f) + movement,
            quality);
        Span<DirectionalShadowCascade> second =
            stackalloc DirectionalShadowCascade[4];
        DirectionalShadowCascadeFitter.Fit(in secondInput, second);

        Assert.Equal(
            first[0].StabilizedLightSpaceCenter.X,
            second[0].StabilizedLightSpaceCenter.X);
        Assert.Equal(
            first[0].StabilizedLightSpaceCenter.Y,
            second[0].StabilizedLightSpaceCenter.Y);
        Assert.Equal(first[0].HalfExtentMeters, second[0].HalfExtentMeters);
    }

    [Fact]
    public void StableLightUp_DoesNotRotateAtTheFormerHighLightThreshold()
    {
        Vector3 below = Vector3.Normalize(new Vector3(0.3125f, 0.02f, 0.9498f));
        Vector3 above = Vector3.Normalize(new Vector3(0.3110f, 0.02f, 0.9503f));

        Vector3 belowUp = DirectionalShadowCascadeFitter.StableLightUp(below);
        Vector3 aboveUp = DirectionalShadowCascadeFitter.StableLightUp(above);

        Assert.InRange(MathF.Abs(Vector3.Dot(below, belowUp)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(above, aboveUp)), 0f, 1e-5f);
        Assert.True(Vector3.Dot(belowUp, aboveUp) > 0.999f);
    }

    [Fact]
    public void StableLightUp_TrueZenithIsFiniteAndOrthogonal()
    {
        Vector3 up = DirectionalShadowCascadeFitter.StableLightUp(Vector3.UnitZ);

        Assert.True(float.IsFinite(up.X) && float.IsFinite(up.Y) && float.IsFinite(up.Z));
        Assert.Equal(1f, up.Length(), 5);
        Assert.InRange(MathF.Abs(Vector3.Dot(Vector3.UnitZ, up)), 0f, 1e-5f);
    }

    [Fact]
    public void StableLightUp_RemainsContinuousThroughCelestialZenith()
    {
        Vector3 beforeZenith = Vector3.Normalize(new Vector3(0.001f, 0.002f, 1f));
        Vector3 zenith = Vector3.UnitZ;
        Vector3 afterZenith = Vector3.Normalize(new Vector3(-0.001f, -0.002f, 1f));

        Vector3 beforeUp = DirectionalShadowCascadeFitter.StableLightUp(beforeZenith);
        Vector3 zenithUp = DirectionalShadowCascadeFitter.StableLightUp(zenith);
        Vector3 afterUp = DirectionalShadowCascadeFitter.StableLightUp(afterZenith);

        Assert.True(Vector3.Dot(beforeUp, zenithUp) > 0.99999f);
        Assert.True(Vector3.Dot(zenithUp, afterUp) > 0.99999f);
        Assert.InRange(MathF.Abs(Vector3.Dot(beforeZenith, beforeUp)), 0f, 1e-5f);
        Assert.InRange(MathF.Abs(Vector3.Dot(afterZenith, afterUp)), 0f, 1e-5f);
    }

    [Fact]
    public void ClipDensityRatio_MatchesCascadeTexelFootprintRatio()
    {
        DirectionalShadowCascadeFitInput input = CameraInput(
            new Vector3(40f, -15f, 8f),
            DirectionalShadowQuality.For(DirectionalShadowPreset.High));
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[4];
        int count = DirectionalShadowCascadeFitter.Fit(in input, cascades);

        float nearDensity = ClipXyDensity(cascades[0].WorldToShadowClip);
        float farDensity = ClipXyDensity(cascades[count - 1].WorldToShadowClip);
        float shaderScale = farDensity / nearDensity;
        float expectedScale = cascades[0].TexelWorldSize
            / cascades[count - 1].TexelWorldSize;

        Assert.Equal(expectedScale, shaderScale, 4);
        Assert.InRange(shaderScale, 0f, 0.999f);
    }

    [Fact]
    public void Fit_DoesNotAllocateOrInvokeSceneVisibility()
    {
        DirectionalShadowCascadeFitInput input = CameraInput(
            Vector3.Zero,
            DirectionalShadowQuality.For(DirectionalShadowPreset.High));
        var cascades = new DirectionalShadowCascade[4];

        ZeroAllocationProbe.AssertAllocatesNothing(
            "DirectionalShadowCascadeFitter.Fit",
            () => DirectionalShadowCascadeFitter.Fit(in input, cascades),
            batchSize: 100);
    }

    [Fact]
    public void ResidentWindowClampsOnlyTheFinalCascadeReach()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.High);
        DirectionalShadowCascadeFitInput input = CameraInput(
            Vector3.Zero,
            quality) with
        {
            ResidentMaximumReachMeters = 96f,
        };
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[4];

        int count = DirectionalShadowCascadeFitter.Fit(in input, cascades);

        Assert.Equal(quality.CascadeCount, count);
        Assert.Equal(96f, cascades[count - 1].SplitFarMeters, 3);
        Assert.All(
            cascades[..count].ToArray(),
            cascade => Assert.InRange(cascade.SplitFarMeters, 0f, 96f));
    }

    [Fact]
    public void UnavailableResidentWindowDisablesFittingWithoutAllocating()
    {
        DirectionalShadowCascadeFitInput input = CameraInput(
            Vector3.Zero,
            DirectionalShadowQuality.For(DirectionalShadowPreset.High)) with
        {
            ResidentMaximumReachMeters = 0f,
        };
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[4];

        Assert.Equal(0, DirectionalShadowCascadeFitter.Fit(in input, cascades));
    }

    [Theory]
    [InlineData(48f, 144f, float.PositiveInfinity, 144f)]
    [InlineData(48f, 144f, 96f, 96f)]
    [InlineData(160f, 144f, 96f, 160f)]
    public void CasterDepthPadding_CoversTheEffectiveResidentReceiverReach(
        float configuredPadding,
        float qualityReach,
        float residentReach,
        float expectedPadding)
    {
        Assert.Equal(
            expectedPadding,
            DirectionalSunShadowRenderer.ResolveCasterDepthPaddingMeters(
                configuredPadding,
                qualityReach,
                residentReach));
    }

    [Fact]
    public void ReachSizedCasterDepth_KeepsLowSunTreeShadowInsideDuringCameraRotation()
    {
        DirectionalShadowQuality quality = DirectionalShadowQuality.For(
            DirectionalShadowPreset.Medium);
        Vector3 light = Vector3.Normalize(new Vector3(0.8f, 0.4f, 0.15f));
        Vector3 receiver = new(30f, 0f, 0f);
        Vector3 caster = receiver + light * 80f;
        int visibleSamples = 0;
        bool legacyPaddingClippedCaster = false;
        float casterDepthPadding =
            DirectionalSunShadowRenderer.ResolveCasterDepthPaddingMeters(
                configuredPaddingMeters: 48f,
                quality.MaximumReachMeters,
                residentMaximumReachMeters: float.PositiveInfinity);
        Assert.Equal(quality.MaximumReachMeters, casterDepthPadding);
        Span<DirectionalShadowCascade> cascades =
            stackalloc DirectionalShadowCascade[4];
        Span<DirectionalShadowCascade> legacyCascades =
            stackalloc DirectionalShadowCascade[4];

        for (int yawDegrees = -50; yawDegrees <= 50; yawDegrees += 5)
        {
            float yaw = yawDegrees * MathF.PI / 180f;
            Vector3 forward = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
            Matrix4x4 view = Matrix4x4.CreateLookAt(
                Vector3.Zero,
                forward,
                Vector3.UnitZ);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
                70f * MathF.PI / 180f,
                16f / 9f,
                0.1f,
                5000f);
            Matrix4x4 viewProjection = view * projection;
            if (!InsideClip(receiver, viewProjection))
                continue;
            visibleSamples++;

            var input = new DirectionalShadowCascadeFitInput(
                view,
                projection,
                light,
                quality,
                CasterDepthPaddingMeters: casterDepthPadding);
            int count = DirectionalShadowCascadeFitter.Fit(in input, cascades);
            DirectionalShadowCascadeFitInput legacyInput = input with
            {
                CasterDepthPaddingMeters = 48f,
            };
            int legacyCount = DirectionalShadowCascadeFitter.Fit(
                in legacyInput,
                legacyCascades);
            Assert.Equal(count, legacyCount);
            for (int cascadeIndex = 0; cascadeIndex < count; cascadeIndex++)
            {
                Assert.Equal(
                    legacyCascades[cascadeIndex].HalfExtentMeters,
                    cascades[cascadeIndex].HalfExtentMeters);
                Assert.Equal(
                    legacyCascades[cascadeIndex].TexelWorldSize,
                    cascades[cascadeIndex].TexelWorldSize);
            }
            DirectionalShadowCascadeBlend selected =
                DirectionalShadowReceiverPolicy.SelectCascade(
                    receiver.Length(),
                    new Vector4(
                        cascades[0].SplitFarMeters,
                        cascades[1].SplitFarMeters,
                        cascades[2].SplitFarMeters,
                        0f),
                    count,
                    blendWidthMeters: 2f);
            legacyPaddingClippedCaster |= !InsideClip(
                caster,
                legacyCascades[selected.PrimaryCascade].WorldToShadowClip);

            Assert.True(
                InsideClip(
                    receiver,
                    cascades[selected.PrimaryCascade].WorldToShadowClip),
                $"receiver left cascade {selected.PrimaryCascade} at yaw {yawDegrees}");
            Assert.True(
                InsideClip(
                    caster,
                    cascades[selected.PrimaryCascade].WorldToShadowClip),
                $"caster left cascade {selected.PrimaryCascade} at yaw {yawDegrees}");
        }

        Assert.True(visibleSamples > 1);
        Assert.True(legacyPaddingClippedCaster);
    }

    private static DirectionalShadowCascadeFitInput CameraInput(
        Vector3 position,
        DirectionalShadowQuality quality)
    {
        Vector3 target = position + Vector3.Normalize(new Vector3(1f, 2f, -0.2f));
        Matrix4x4 view = Matrix4x4.CreateLookAt(position, target, Vector3.UnitZ);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            70f * MathF.PI / 180f,
            16f / 9f,
            0.1f,
            5000f);
        return new DirectionalShadowCascadeFitInput(
            view,
            projection,
            Vector3.Normalize(new Vector3(0.4f, 0.7f, 0.55f)),
            quality);
    }

    private static float ClipXyDensity(Matrix4x4 matrix)
    {
        float x = new Vector3(matrix.M11, matrix.M21, matrix.M31).Length();
        float y = new Vector3(matrix.M12, matrix.M22, matrix.M32).Length();
        return 0.5f * (x + y);
    }

    private static bool InsideClip(Vector3 point, Matrix4x4 transform)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), transform);
        if (!float.IsFinite(clip.W) || MathF.Abs(clip.W) <= 1e-6f)
            return false;
        Vector3 ndc = new(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        return MathF.Abs(ndc.X) <= 1f
            && MathF.Abs(ndc.Y) <= 1f
            && ndc.Z is >= 0f and <= 1f;
    }
}
