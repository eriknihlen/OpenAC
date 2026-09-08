using System;
using System.Collections.Generic;

namespace AcDream.Core.Lighting;

public static class GlobalLightPacker
{
    public const int FloatsPerLight = 16;

    public static int Pack(IReadOnlyList<LightSource>? snapshot, ref float[] buffer)
    {
        int n = snapshot?.Count ?? 0;
        int floatsNeeded = Math.Max(n, 1) * FloatsPerLight;
        if (buffer.Length < floatsNeeded)
            buffer = new float[floatsNeeded + FloatsPerLight * 16];
        Array.Clear(buffer, 0, floatsNeeded);

        for (int i = 0; i < n; i++)
        {
            var L = snapshot![i];
            int o = i * FloatsPerLight;
            // posAndKind (xyz world pos, w kind)
            buffer[o + 0] = L.WorldPosition.X;
            buffer[o + 1] = L.WorldPosition.Y;
            buffer[o + 2] = L.WorldPosition.Z;
            buffer[o + 3] = (int)L.Kind;
            // dirAndRange (xyz forward, w range)
            buffer[o + 4] = L.WorldForward.X;
            buffer[o + 5] = L.WorldForward.Y;
            buffer[o + 6] = L.WorldForward.Z;
            buffer[o + 7] = L.Range;   // w = Range = Falloff × static_light_factor (1.3), pre-multiplied by LightInfoLoader — NOT the raw dat Falloff
            buffer[o + 8]  = L.ColorLinear.X;
            buffer[o + 9]  = L.ColorLinear.Y;
            buffer[o + 10] = L.ColorLinear.Z;
            buffer[o + 11] = L.Intensity;
            buffer[o + 12] = L.ConeAngle;
            buffer[o + 13] = L.IsDynamic ? 1f : 0f;   // shader: 1/d D3D attenuation vs static 1/d³
        }
        return n;
    }
}
