using System.Numerics;

namespace AcDream.App.Rendering;

internal static class RetailDetailTextureContract
{
    internal const int NoFogRenderPassFlag = 0x200;

    internal static bool ShouldRender(
        bool settingEnabled,
        TerrainAtlas.RetailDetailTextureBinding binding) =>
        settingEnabled && binding.IsAvailable;

    internal static Vector4 Combine(
        Vector4 baseTexel,
        Vector3 diffuse,
        Vector4 detail,
        float authoredOpacity,
        float liveOpacity)
    {
        float alpha = authoredOpacity * liveOpacity;
        float weight = alpha * detail.W;
        Vector3 colour = new(detail.X, detail.Y, detail.Z);
        colour *= weight;
        colour += new Vector3(baseTexel.X, baseTexel.Y, baseTexel.Z)
            * diffuse * (1f - weight);
        float outputAlpha = alpha * detail.W * detail.W;
        return new Vector4(colour, outputAlpha);
    }

    internal static Vector4 ApplyFog(Vector4 material, Vector3 fog, float fogFactor) =>
        new(Vector3.Lerp(new Vector3(material.X, material.Y, material.Z), fog, fogFactor), material.W);

    /// <summary>
    /// Mirrors D3D's GREATER_EQUAL alpha test: equality survives.
    /// </summary>
    internal static bool SurvivesClip(float outputAlpha, float reference) =>
        outputAlpha >= reference;

    internal enum FramebufferFamily
    {
        Opaque,
        Alpha,
        AlphaAdditive,
        Additive,
        InverseAlpha,
        InverseAlphaAdditive,
        Clip,
    }

    internal static Vector4 Composite(
        Vector4 source,
        Vector4 destination,
        FramebufferFamily family)
    {
        float x = source.W;
        return family switch
        {
            FramebufferFamily.Opaque => source,
            FramebufferFamily.Alpha => source * x + destination * (1f - x),
            FramebufferFamily.AlphaAdditive => source * x + destination,
            FramebufferFamily.Additive => source + destination,
            FramebufferFamily.InverseAlpha => source * (1f - x) + destination * x,
            FramebufferFamily.InverseAlphaAdditive => source * (1f - x) + destination,
            FramebufferFamily.Clip => source + destination * new Vector4(1f - x),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };
    }
}
