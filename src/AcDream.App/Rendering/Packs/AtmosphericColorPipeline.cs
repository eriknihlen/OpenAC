using System.Numerics;

namespace AcDream.App.Rendering.Packs;

internal static class AtmosphericColorPipeline
{
    internal const float DisplayGamma = 2.2f;

    internal const float LinearMidGrey = 0.18f;

    private static readonly Vector3 Rec709Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>Mirrors <c>acdreamDecodeDisplay</c>: display-space to linear light.</summary>
    internal static Vector3 Decode(Vector3 c) =>
        Pow(Vector3.Max(c, Vector3.Zero), DisplayGamma);

    /// <summary>Mirrors <c>acdreamEncodeDisplay</c>: linear light to display-space.</summary>
    internal static Vector3 Encode(Vector3 c) =>
        Pow(Vector3.Max(c, Vector3.Zero), 1f / DisplayGamma);

    internal static Vector3 AcesFitted(Vector3 value)
    {
        const float a = 2.51f;
        const float b = 0.03f;
        const float c = 2.43f;
        const float d = 0.59f;
        const float e = 0.14f;
        Vector3 numerator = value * (a * value + new Vector3(b));
        Vector3 denominator = value * (c * value + new Vector3(d)) + new Vector3(e);
        return Vector3.Clamp(numerator / denominator, Vector3.Zero, Vector3.One);
    }

    internal static Vector3 Grade(Vector3 color, float saturation, float contrast)
    {
        float luminance = Vector3.Dot(color, Rec709Luma);
        color = Vector3.Lerp(new Vector3(luminance), color, saturation);
        return (color - new Vector3(LinearMidGrey)) * contrast + new Vector3(LinearMidGrey);
    }

    internal static Vector3 Filmic(
        Vector3 hdr,
        float exposure,
        float filmicStrength,
        float saturation,
        float contrast,
        float vignetteFactor)
    {
        Vector3 exposed = Vector3.Max(hdr * exposure, Vector3.Zero);
        Vector3 linearClamped = Vector3.Clamp(exposed, Vector3.Zero, Vector3.One);
        Vector3 color = Vector3.Lerp(
            linearClamped,
            AcesFitted(exposed),
            Math.Clamp(filmicStrength, 0f, 1f));
        color = Grade(color, saturation, contrast);
        color *= vignetteFactor;
        return Vector3.Clamp(color, Vector3.Zero, Vector3.One);
    }

    private static Vector3 Pow(Vector3 value, float exponent) => new(
        MathF.Pow(value.X, exponent),
        MathF.Pow(value.Y, exponent),
        MathF.Pow(value.Z, exponent));
}
