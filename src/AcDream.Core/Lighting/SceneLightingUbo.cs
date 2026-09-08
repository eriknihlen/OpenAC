using System;
using System.Numerics;
using System.Runtime.InteropServices;
using AcDream.Core.World;

namespace AcDream.Core.Lighting;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UboLight
{
    public Vector4 PosAndKind;
    public Vector4 DirAndRange;
    public Vector4 ColorAndIntensity;
    public Vector4 ConeAngleEtc;

    public static UboLight FromSource(LightSource ls)
    {
        return new UboLight
        {
            PosAndKind        = new Vector4(ls.WorldPosition, (float)(int)ls.Kind),
            DirAndRange       = new Vector4(ls.WorldForward,  ls.Range),
            ColorAndIntensity = new Vector4(ls.ColorLinear,   ls.Intensity),
            ConeAngleEtc      = new Vector4(ls.ConeAngle,     0f, 0f, 0f),
        };
    }

    public static UboLight Empty => new()
    {
        PosAndKind        = Vector4.Zero,
        DirAndRange       = Vector4.Zero,
        ColorAndIntensity = Vector4.Zero,
        ConeAngleEtc      = Vector4.Zero,
    };
}

/// <summary>
/// Full CPU-side scene-lighting UBO buffer. One per frame; lives on the
/// render thread. The GL-side wrapper (<c>SceneLightingUboBinding</c>
/// in AcDream.App) uploads this to binding=1 once per frame.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct SceneLightingUbo
{
    // 8 lights × 64 bytes = 512 bytes
    public UboLight Light0;
    public UboLight Light1;
    public UboLight Light2;
    public UboLight Light3;
    public UboLight Light4;
    public UboLight Light5;
    public UboLight Light6;
    public UboLight Light7;

    public Vector4 CellAmbient;
    public Vector4 FogParams;        // x = fogStart, y = fogEnd, z = flash, w = fogMode
    public Vector4 FogColor;
    public Vector4 CameraAndTime;

    public const int SizeInBytes = 8 * 64 + 4 * 16;  // 576
    public const int BindingPoint = 1;

    public static SceneLightingUbo Build(
        LightManager lights,
        in AtmosphereSnapshot atmo,
        Vector3 cameraWorldPos,
        float dayFraction)
    {
        ArgumentNullException.ThrowIfNull(lights);

        var ubo = new SceneLightingUbo();

        // Pack up to 8 lights. Empty slots stay zero.
        var active = lights.Active;
        int count = active.Length;
        if (count > 8) count = 8;
        for (int i = 0; i < 8; i++)
        {
            var packed = (i < count && active[i] is not null)
                ? UboLight.FromSource(active[i]!)
                : UboLight.Empty;
            SetLightAt(ref ubo, i, packed);
        }

        ubo.CellAmbient = new Vector4(lights.CurrentAmbient.AmbientColor, count);
        ubo.FogParams   = new Vector4(
            atmo.FogStart,
            atmo.FogEnd,
            atmo.LightningFlash,
            (float)(int)atmo.FogMode);
        ubo.FogColor    = new Vector4(atmo.FogColor, 0f);
        ubo.CameraAndTime = new Vector4(cameraWorldPos, dayFraction);
        return ubo;
    }

    private static void SetLightAt(ref SceneLightingUbo ubo, int i, in UboLight v)
    {
        switch (i)
        {
            case 0: ubo.Light0 = v; break;
            case 1: ubo.Light1 = v; break;
            case 2: ubo.Light2 = v; break;
            case 3: ubo.Light3 = v; break;
            case 4: ubo.Light4 = v; break;
            case 5: ubo.Light5 = v; break;
            case 6: ubo.Light6 = v; break;
            case 7: ubo.Light7 = v; break;
        }
    }
}
