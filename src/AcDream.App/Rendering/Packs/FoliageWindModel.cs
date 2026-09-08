using System.Numerics;
using AcDream.App.Rendering.Wb;

namespace AcDream.App.Rendering.Packs;

internal static class FoliageWindModel
{
    private const uint FoliageMask =
        FoliageWindClassification.CutoutFoliageFlag | FoliageWindClassification.TrunkFlag;

    internal static Vector3 Displace(
        Vector3 worldPos,
        Vector3 instanceOrigin,
        uint batchFlags,
        Vector4 clockWind,
        Vector4 amplitude)
    {
        if ((batchFlags & FoliageMask) == 0u)
            return worldPos;

        float t = clockWind.X;
        float mean = clockWind.Y;
        float gust = clockWind.Z;
        float dirA = clockWind.W;

        var dir = new Vector2(MathF.Cos(dirA), MathF.Sin(dirA));
        var perp = new Vector2(-dir.Y, dir.X);

        float h = Math.Clamp(
            (worldPos.Z - instanceOrigin.Z) / MathF.Max(amplitude.W, 0.5f),
            0f,
            1f);
        float k = h * h;

        float ph = Vector2.Dot(
            new Vector2(instanceOrigin.X, instanceOrigin.Y),
            new Vector2(0.137f, 0.291f));

        float g = 0.5f
            + (0.5f * MathF.Sin((0.05f * t) + ph))
            + (0.25f * MathF.Sin((0.13f * t) + (1.7f * ph)));
        float s = mean + (gust * g);

        float lean = k * amplitude.X * s * (0.8f + (0.2f * MathF.Sin((0.35f * t) + ph)));
        Vector2 d = dir * lean;

        if ((batchFlags & FoliageWindClassification.CutoutFoliageFlag) != 0u)
        {
            float branch = k * amplitude.Y * s * MathF.Sin((1.1f * t) + ph + (2.0f * h));
            var relativeXY = new Vector2(
                worldPos.X - instanceOrigin.X,
                worldPos.Y - instanceOrigin.Y);
            float vh = Frac(
                MathF.Sin(Vector2.Dot(relativeXY, new Vector2(12.9898f, 78.233f))) * 43758.5453f);
            float flutter = h * amplitude.Z * s * MathF.Sin((6.0f * t) + (7.0f * vh));
            d += (dir * branch)
                + (perp * 0.35f * branch)
                + (new Vector2(MathF.Cos(6.2832f * vh), MathF.Sin(6.2832f * vh)) * flutter);
        }

        Vector3 p = worldPos;
        p.X += d.X;
        p.Y += d.Y;
        p.Z -= 0.5f * Vector2.Dot(d, d) / MathF.Max(h * amplitude.W, 0.5f);
        return p;
    }

    /// <summary>GLSL <c>fract</c>: always non-negative, matches <c>x - floor(x)</c>.</summary>
    private static float Frac(float x) => x - MathF.Floor(x);
}
