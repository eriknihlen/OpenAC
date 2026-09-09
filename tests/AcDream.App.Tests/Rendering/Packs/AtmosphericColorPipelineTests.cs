using System.Numerics;
using AcDream.App.Rendering.Packs;

namespace AcDream.App.Tests.Rendering.Packs;

public sealed class AtmosphericColorPipelineTests
{
    private const float EightBitHalfStep = 1f / 510f;

    [Fact]
    public void NeutralPresetReproducesThePackOffPixelWithinHalfAnEightBitStep()
    {
        for (int step = 0; step <= 255; step++)
        {
            float c = step / 255f;
            var pixel = new Vector3(c, c, c);
            Vector3 result = AtmosphericColorPipeline.Encode(
                AtmosphericColorPipeline.Filmic(
                    AtmosphericColorPipeline.Decode(pixel),
                    exposure: 1f,
                    filmicStrength: 0f,
                    saturation: 1f,
                    contrast: 1f,
                    vignetteFactor: 1f));

            Assert.True(
                MathF.Abs(result.X - c) < EightBitHalfStep,
                $"channel diverged at c={c}: got {result.X}");
            Assert.True(
                MathF.Abs(result.Y - c) < EightBitHalfStep,
                $"channel diverged at c={c}: got {result.Y}");
            Assert.True(
                MathF.Abs(result.Z - c) < EightBitHalfStep,
                $"channel diverged at c={c}: got {result.Z}");
        }
    }

    [Fact]
    public void DecodeAndEncodeRoundTripWithinFloatPrecision()
    {
        for (int step = 0; step <= 255; step++)
        {
            float c = step / 255f;
            var pixel = new Vector3(c, c, c);

            Vector3 decodedThenEncoded = AtmosphericColorPipeline.Encode(
                AtmosphericColorPipeline.Decode(pixel));
            Vector3 encodedThenDecoded = AtmosphericColorPipeline.Decode(
                AtmosphericColorPipeline.Encode(pixel));

            Assert.True(MathF.Abs(decodedThenEncoded.X - c) < 1e-6f);
            Assert.True(MathF.Abs(encodedThenDecoded.X - c) < 1e-6f);
        }
    }

    [Fact]
    public void FilmicOutputIsMonotonicNonDecreasingInExposure()
    {
        Vector3 hdr = AtmosphericColorPipeline.Decode(new Vector3(0.5f, 0.5f, 0.5f));
        float previous = -1f;
        for (float exposure = 0.1f; exposure <= 3.0f; exposure += 0.1f)
        {
            Vector3 result = AtmosphericColorPipeline.Filmic(
                hdr,
                exposure,
                filmicStrength: 1f,
                saturation: 1f,
                contrast: 1f,
                vignetteFactor: 1f);

            Assert.True(
                result.X >= previous - EightBitHalfStep,
                $"exposure={exposure}: {result.X} < previous {previous}");
            previous = result.X;
        }
    }

    [Fact]
    public void AcceptedExposurePointReproducesTheOwnerGatedMidtone()
    {
        Vector3 midtone = AtmosphericColorPipeline.Encode(
            AtmosphericColorPipeline.Filmic(
                AtmosphericColorPipeline.Decode(new Vector3(0.46f, 0.46f, 0.46f)),
                exposure: 0.80f,
                filmicStrength: 1f,
                saturation: 1f,
                contrast: 1f,
                vignetteFactor: 1f));
        Assert.InRange(midtone.X, 0.50f - 0.02f, 0.50f + 0.02f);

        // Highlights retain more than the old gamma-space pipeline.
        Vector3 highlight = AtmosphericColorPipeline.Encode(
            AtmosphericColorPipeline.Filmic(
                AtmosphericColorPipeline.Decode(new Vector3(0.9f, 0.9f, 0.9f)),
                exposure: 0.80f,
                filmicStrength: 1f,
                saturation: 1f,
                contrast: 1f,
                vignetteFactor: 1f));
        Assert.InRange(highlight.X, 0.85f - 0.02f, 0.85f + 0.02f);

        // Blacks deepen slightly relative to the old gamma-space pipeline.
        Vector3 shadow = AtmosphericColorPipeline.Encode(
            AtmosphericColorPipeline.Filmic(
                AtmosphericColorPipeline.Decode(new Vector3(0.1f, 0.1f, 0.1f)),
                exposure: 0.80f,
                filmicStrength: 1f,
                saturation: 1f,
                contrast: 1f,
                vignetteFactor: 1f));
        Assert.InRange(shadow.X, 0.05f - 0.02f, 0.05f + 0.02f);
    }

    [Fact]
    public void BloomKneeDerivationMatchesTheGraphsLinearConstant()
    {
        // Pre-VM3 gamma-space soft range was [0.55, 1.0] (threshold 1.0,
        // knee 0.45). Decoding both ends with the same 2.2 assumption gives
        // the linear range this pack now uses.
        float decodedLowerBound = MathF.Pow(0.55f, AtmosphericColorPipeline.DisplayGamma);
        Assert.InRange(decodedLowerBound, 0.27f - 0.01f, 0.27f + 0.01f);

        float derivedKnee = AtmosphericPostProcessGraph.BloomThresholdLinear - decodedLowerBound;
        Assert.InRange(derivedKnee, 0.73f - 0.01f, 0.73f + 0.01f);
        Assert.Equal(AtmosphericPostProcessGraph.BloomKneeLinear, derivedKnee, 2);
        Assert.Equal(1f, AtmosphericPostProcessGraph.BloomThresholdLinear);
    }

    [Fact]
    public void VignetteDefaultReproducesTheAcceptedTwelvePercentCornerDarkening()
    {
        float VignetteStrengthDefault = ShippedDefault("vignette-strength");
        Assert.Equal(0.245f, VignetteStrengthDefault, 3);
        float cornerMultiplier = 1f - VignetteStrengthDefault;
        Vector3 displayed = AtmosphericColorPipeline.Encode(
            new Vector3(cornerMultiplier, cornerMultiplier, cornerMultiplier));
        Assert.InRange(displayed.X, 0.88f - 0.005f, 0.88f + 0.005f);
    }
    private static float ShippedDefault(string settingId)
    {
        AcDream.Plugin.Abstractions.Rendering.RenderSettingDeclaration declaration =
            Assert.Single(
                AcDream.App.Rendering.Packs.BuiltInAtmosphericRenderPack.Descriptor.Settings,
                s => s.Id == settingId);
        return float.Parse(
            declaration.DefaultValue,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ShippedExposureDefaultIsTheDerivedMidtoneValue()
    {
        // 0.80 is the value whose linear-light result reproduces the accepted
        // mid-grey (see BuiltInAtmosphericRenderPack "exposure" derivation).
        Assert.Equal(0.80f, ShippedDefault("exposure"), 3);
        Assert.Equal(
            0.245f,
            AcDream.App.Rendering.Packs.AtmosphericPostProcessGraph.DefaultVignetteStrengthFallback, 3);
    }
}

