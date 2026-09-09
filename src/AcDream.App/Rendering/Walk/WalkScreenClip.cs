using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public struct WalkScreenPoint
{
    public float X, Y, Z, W;

    public WalkScreenPoint(float x, float y, float z, float w)
    {
        X = x; Y = y; Z = z; W = w;
    }
}

public static class WalkScreenClip
{
    public const float MinW = WalkVisibilityMath.Epsilon;

    public static WalkScreenPoint TransformToScreen(
        Vector3 point, in Matrix4x4 objectToClip, float viewportWidth, float viewportHeight)
    {
        Vector4 clip = Vector4.Transform(new Vector4(point, 1f), objectToClip);
        return new WalkScreenPoint(
            clip.X * viewportWidth * 0.5f + clip.W * viewportWidth * 0.5f,
            clip.W * viewportHeight * 0.5f - clip.Y * viewportHeight * 0.5f,
            clip.Z,
            clip.W);
    }

    public static int ClipAgainstView(
        ReadOnlySpan<WalkScreenPoint> input,
        ReadOnlySpan<Vector2> viewEdgeVertices,
        Span<WalkScreenPoint> output)
    {
        Span<WalkScreenPoint> bufferA = stackalloc WalkScreenPoint[64];
        Span<WalkScreenPoint> bufferB = stackalloc WalkScreenPoint[64];
        Span<WalkScreenPoint> current = bufferA;
        int count = input.Length;
        input.CopyTo(current);
        int reversals = 0;

        bool anyBelow = false;
        for (int i = 0; i < count; i++)
            if (current[i].W < MinW) { anyBelow = true; break; }
        if (anyBelow)
        {
            count = ClipPassW(current[..count], bufferB);
            if (count < 3) return 0;
            Span<WalkScreenPoint> swap = current;
            current = bufferB;
            bufferB = swap;
            reversals++;
        }

        // Edge passes: pairs (a, b) = (v[0], v[n-1]), (v[n-1], v[n-2]) … (v[1], v[0]).
        int n = viewEdgeVertices.Length;
        for (int e = n - 1; e >= 0; e--)
        {
            Vector2 a = viewEdgeVertices[e == n - 1 ? 0 : e + 1];
            Vector2 b = viewEdgeVertices[e];
            count = ClipPassEdge(current[..count], a, b, bufferB);
            if (count < 3) return 0;
            Span<WalkScreenPoint> swap = current;
            current = bufferB;
            bufferB = swap;
            reversals++;
        }

        // Restore original winding: each pass reversed the order once.
        if ((reversals & 1) != 0)
        {
            for (int i = 0; i < count; i++)
                output[i] = current[count - 1 - i];
        }
        else
        {
            current[..count].CopyTo(output);
        }
        return count;
    }

    private static int ClipPassW(ReadOnlySpan<WalkScreenPoint> pts, Span<WalkScreenPoint> outPts)
    {
        int outCount = 0;
        // Reverse traversal starting from the wrap pair (pts[0], pts[n-1]).
        WalkScreenPoint prev = pts[0];
        float sPrev = prev.W - MinW;
        bool inPrev = sPrev >= 0f;
        for (int i = pts.Length - 1; i >= 0; i--)
        {
            WalkScreenPoint cur = pts[i];
            float s = cur.W - MinW;
            bool inCur = s >= 0f;
            if (inPrev != inCur)
                outPts[outCount++] = Lerp(prev, cur, sPrev / (sPrev - s));
            if (inCur)
                outPts[outCount++] = cur;
            prev = cur; sPrev = s; inPrev = inCur;
        }
        return outCount;
    }

    private static int ClipPassEdge(
        ReadOnlySpan<WalkScreenPoint> pts, Vector2 a, Vector2 b, Span<WalkScreenPoint> outPts)
    {
        float ex = b.X - a.X;
        float ey = b.Y - a.Y;
        float Side(in WalkScreenPoint p) => (p.X - a.X * p.W) * ey - (p.Y - a.Y * p.W) * ex;

        int outCount = 0;
        WalkScreenPoint prev = pts[0];
        float s0 = Side(prev);
        float sPrev = s0;
        bool inPrev = s0 <= 0f;
        for (int i = pts.Length - 1; i >= 0; i--)
        {
            WalkScreenPoint cur = pts[i];
            float s = i != 0 ? Side(cur) : s0;   // final pair reuses point 0's side
            bool inCur = s <= 0f;
            if (inPrev != inCur)
                outPts[outCount++] = Lerp(prev, cur, sPrev / (sPrev - s));
            if (inCur)
                outPts[outCount++] = cur;
            prev = cur; sPrev = s; inPrev = inCur;
        }
        return outCount;
    }

    private static WalkScreenPoint Lerp(in WalkScreenPoint p, in WalkScreenPoint q, float t)
        => new(
            p.X + (q.X - p.X) * t,
            p.Y + (q.Y - p.Y) * t,
            p.Z + (q.Z - p.Z) * t,
            p.W + (q.W - p.W) * t);
}
