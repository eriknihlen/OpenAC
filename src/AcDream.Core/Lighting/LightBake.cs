using System;
using System.Collections.Generic;
using System.Numerics;

namespace AcDream.Core.Lighting;

public static class LightBake
{
    private const float TwoLpr   = 1.5f;   // LIGHT_POINT_RANGE + LIGHT_POINT_RANGE
    private const float WrapBias = 0.5f;   // (2 · LIGHT_POINT_RANGE) − 1.0

    public static Vector3 PointContribution(
        Vector3 vtxWorldPos, Vector3 vtxWorldNormal, LightSource light)
    {
        // D = light − vertex (FROM vertex TO light), used un-normalised.
        float dx = light.WorldPosition.X - vtxWorldPos.X;
        float dy = light.WorldPosition.Y - vtxWorldPos.Y;
        float dz = light.WorldPosition.Z - vtxWorldPos.Z;

        float distsq = dx * dx + dy * dy + dz * dz;
        float dist   = MathF.Sqrt(distsq);
        float falloffEff = light.Range;             // = Falloff × static_light_factor(1.3)
        if (dist >= falloffEff || falloffEff <= 1e-4f)
            return Vector3.Zero;

        // Half-Lambert wrap: (1/1.5)·(N·D + 0.5·dist), N un-normalised vertex normal.
        float wrap = (1f / TwoLpr) *
            (vtxWorldNormal.X * dx + vtxWorldNormal.Y * dy + vtxWorldNormal.Z * dz
             + WrapBias * dist);
        if (wrap <= 0f)
            return Vector3.Zero;

        float norm  = distsq > 1f ? distsq * dist : dist;
        float scale = (1f - dist / falloffEff) * light.Intensity * (wrap / norm);

        return new Vector3(
            MathF.Min(scale * light.ColorLinear.X, light.ColorLinear.X),
            MathF.Min(scale * light.ColorLinear.Y, light.ColorLinear.Y),
            MathF.Min(scale * light.ColorLinear.Z, light.ColorLinear.Z));
    }

    public static Vector3 ComputeVertexColor(
        Vector3 vtxWorldPos, Vector3 vtxWorldNormal, IReadOnlyList<LightSource> reaching)
    {
        float r = 0f, g = 0f, b = 0f;
        for (int i = 0; i < reaching.Count; i++)
        {
            var light = reaching[i];
            if (!light.IsLit || light.Kind == LightKind.Directional) continue;
            var c = PointContribution(vtxWorldPos, vtxWorldNormal, light);
            r += c.X; g += c.Y; b += c.Z;
        }
        return new Vector3(
            Math.Clamp(r, 0f, 1f),
            Math.Clamp(g, 0f, 1f),
            Math.Clamp(b, 0f, 1f));
    }
}
