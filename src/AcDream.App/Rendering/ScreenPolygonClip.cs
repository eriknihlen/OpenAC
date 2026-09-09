using System.Collections.Generic;
using System.Numerics;

namespace AcDream.App.Rendering;

public static class ScreenPolygonClip
{
    private const float Eps = 1e-7f;

    public static Vector2[] Intersect(IReadOnlyList<Vector2> subject, IReadOnlyList<Vector2> clip)
    {
        if (subject == null || clip == null || subject.Count < 3 || clip.Count < 3)
            return System.Array.Empty<Vector2>();

        var output = new List<Vector2>(subject);

        for (int i = 0; i < clip.Count; i++)
        {
            if (output.Count < 3) return System.Array.Empty<Vector2>();

            Vector2 a = clip[i];
            Vector2 b = clip[(i + 1) % clip.Count];
            output = ClipByEdge(output, a, b);
        }

        return output.Count >= 3 ? output.ToArray() : System.Array.Empty<Vector2>();
    }

    // Keep the part of `poly` on the left of directed edge a->b (CCW inside).
    private static List<Vector2> ClipByEdge(List<Vector2> poly, Vector2 a, Vector2 b)
    {
        var result = new List<Vector2>(poly.Count + 1);
        Vector2 edge = b - a;

        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 cur = poly[i];
            Vector2 prev = poly[(i + poly.Count - 1) % poly.Count];

            float curSide = Cross(edge, cur - a);   // > 0 = left (inside)
            float prevSide = Cross(edge, prev - a);

            bool curIn = curSide >= -Eps;
            bool prevIn = prevSide >= -Eps;

            if (curIn)
            {
                if (!prevIn)
                    result.Add(Intersection(prev, cur, prevSide, curSide));
                result.Add(cur);
            }
            else if (prevIn)
            {
                result.Add(Intersection(prev, cur, prevSide, curSide));
            }
        }
        return result;
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    private static Vector2 Intersection(Vector2 p, Vector2 q, float dp, float dq)
    {
        float t = dp / (dp - dq);
        return p + t * (q - p);
    }
}
