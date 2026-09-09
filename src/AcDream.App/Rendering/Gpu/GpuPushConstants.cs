using System.Numerics;
using System.Runtime.InteropServices;

namespace AcDream.App.Rendering.Gpu;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuPushConstants
{
    public Matrix4x4 ViewProjection;

    public int DrawIdOffset;

    /// <summary>GLSL <c>uLightingMode</c>: 0 = object (plain Lambert + sun), 1 = EnvCell (half-Lambert wrap, no sun).</summary>
    public int LightingMode;

    /// <summary>GLSL <c>uRenderPass</c>: 0 = opaque, 1 = translucent.</summary>
    public int RenderPass;

    public int LightDebug;

    public uint TextureIndexA;

    public uint TextureIndexB;

    public float ParamA;

    /// <summary>GLSL <c>uParamB</c>. Spare scalar; unclaimed at V0.</summary>
    public float ParamB;

    /// <summary>Neutral defaults: identity transform, opaque object lighting, no debug mode.</summary>
    public static GpuPushConstants Default => new()
    {
        ViewProjection = Matrix4x4.Identity,
        DrawIdOffset = 0,
        LightingMode = 0,
        RenderPass = 0,
        LightDebug = 0,
        TextureIndexA = 0,
        TextureIndexB = 0,
        ParamA = 0f,
        ParamB = 0f,
    };
}
