using System.Numerics;

namespace AcDream.App.Rendering.Walk;

public struct WalkViewVertex
{
    public Vector2 Point;
    public WalkPlane Plane;
}

public readonly record struct WalkViewPoly(
    int VertexCount, int VertexIndex, float XMin, float XMax, float YMin, float YMax);

public sealed class WalkViewSet
{
    public readonly List<WalkViewPoly> Polys = new();
    public readonly List<WalkViewVertex> Vertices = new();
    public int VertexCountTotal;
}

public sealed class WalkPortalView
{
    public WalkPortalFlags[] PortalFlags = [];

    public readonly WalkViewSet View = new();

    public float MaxInDistSquared;

    public int ViewCount;
    public bool CellViewDone;
    public int ViewTimestamp;
    public int UpdateCount;

    public void ResetForPush()
    {
        ViewCount = 0;
        UpdateCount = 0;
        ViewTimestamp = 0;
    }
}

public struct WalkPortalFlags
{
    public bool Seen;
    public bool InView;
}

public interface IWalkRayCaster
{
    Vector3 RayThrough(float screenX, float screenY);
}

public static class WalkCopyView
{
    public const int MaxVertices = 31;
    public const float DedupThreshold = 1f; // strict > 1 px

    public static bool AppendFullViewportQuad(
        WalkPortalView dest, IWalkRayCaster rays, Vector3 viewpoint,
        float viewportWidth, float viewportHeight)
    {
        Span<WalkScreenPoint> quad =
        [
            new(0f, viewportHeight, 0f, 1f),
            new(viewportWidth, viewportHeight, 0f, 1f),
            new(viewportWidth, 0f, 0f, 1f),
            new(0f, 0f, 0f, 1f),
        ];
        return Append(dest, quad, rays, viewpoint);
    }

    public static bool Append(
        WalkPortalView dest, Span<WalkScreenPoint> points,
        IWalkRayCaster rays, Vector3 viewpoint)
    {
        int npts = points.Length;
        if (npts == 0) return false;

        // ---- survivor marking (keep[] / last / stl / second bookkeeping) ----
        Span<bool> keep = stackalloc bool[npts];
        keep[0] = true;
        int n = 1;
        int last = 0;
        int secondToLast = 0;
        int second = 0;

        for (int i = 0; i < npts; i++)
        {
            ref WalkScreenPoint p = ref points[i];
            if (p.W != 1f)
            {
                p.X /= p.W;
                p.Y /= p.W;
                p.W = 1f;
            }
            if (i == 0) continue;

            bool distinct =
                MathF.Abs(points[i].X - points[last].X) > DedupThreshold
                || MathF.Abs(points[i].Y - points[last].Y) > DedupThreshold;
            keep[i] = distinct;
            if (!distinct) continue;

            if (n == 1)
            {
                n++;
                second = i;
            }
            else
            {
                WalkScreenPoint pp = points[secondToLast];
                WalkScreenPoint prev = points[last];
                WalkScreenPoint cur = points[i];
                float span = MathF.Max(MathF.Abs(pp.X - cur.X), MathF.Abs(pp.Y - cur.Y));
                float cross = (pp.X - prev.X) * (prev.Y - cur.Y)
                              - (pp.Y - prev.Y) * (prev.X - cur.X);
                if (MathF.Abs(cross) >= span)
                {
                    n++;
                    secondToLast = last;
                }
                else
                {
                    keep[last] = false;
                    if (second == last) second = i;
                }
            }
            last = i;
        }

        WalkScreenPoint first = points[0];
        bool lastDistinct =
            MathF.Abs(first.X - points[last].X) > DedupThreshold
            || MathF.Abs(first.Y - points[last].Y) > DedupThreshold;
        keep[last] = lastDistinct;
        if (!lastDistinct)
        {
            n--;
            last = secondToLast;
        }
        else
        {
            float span = MathF.Max(
                MathF.Abs(points[secondToLast].X - first.X),
                MathF.Abs(points[secondToLast].Y - first.Y));
            float cross = (points[secondToLast].X - points[last].X) * (points[last].Y - first.Y)
                          - (points[last].X - first.X) * (points[secondToLast].Y - points[last].Y);
            if (MathF.Abs(cross) < span)
            {
                keep[last] = false;
                n--;
                last = secondToLast;
            }
        }
        secondToLast = last;
        if (second > 0)
        {
            float span = MathF.Max(
                MathF.Abs(points[secondToLast].X - points[second].X),
                MathF.Abs(points[secondToLast].Y - points[second].Y));
            float cross = (first.Y - points[second].Y) * (points[secondToLast].X - first.X)
                          - (first.X - points[second].X) * (points[secondToLast].Y - first.Y);
            if (MathF.Abs(cross) < span)
            {
                n--;
                keep[0] = false;
            }
        }

        if (n < 3) return false;
        if (n > MaxVertices) n = MaxVertices;

        // ---- append into the pool (view_count==0 resets the pool base) ----
        WalkViewSet view = dest.View;
        if (dest.ViewCount == 0)
        {
            view.Polys.Clear();
            view.Vertices.Clear();
            view.VertexCountTotal = 0;
        }
        int vbase = view.VertexCountTotal;
        view.VertexCountTotal = vbase + n + 1;

        int written = 0;
        for (int i = 0; i < npts && written < n; i++)
        {
            if (!keep[i]) continue;
            view.Vertices.Add(new WalkViewVertex
            {
                Point = new Vector2(MathF.Abs(points[i].X), MathF.Abs(points[i].Y)),
            });
            written++;
        }
        view.Vertices.Add(new WalkViewVertex { Point = view.Vertices[vbase].Point });

        // ---- bounds over v[0..n-1] ----
        float xmin, xmax, ymin, ymax;
        Vector2 seed = view.Vertices[vbase + n - 1].Point;
        xmin = xmax = seed.X;
        ymin = ymax = seed.Y;
        for (int k = n - 2; k >= 0; k--)
        {
            Vector2 pt = view.Vertices[vbase + k].Point;
            if (pt.X < xmin) xmin = pt.X; else if (pt.X > xmax) xmax = pt.X;
            if (pt.Y < ymin) ymin = pt.Y; else if (pt.Y > ymax) ymax = pt.Y;
        }
        view.Polys.Add(new WalkViewPoly(n, vbase, xmin, xmax, ymin, ymax));

        // ---- per-edge world planes from unprojected rays ----
        Span<Vector3> ray = stackalloc Vector3[n + 1];
        for (int k = 0; k < n; k++)
        {
            Vector2 pt = view.Vertices[vbase + k].Point;
            ray[k] = rays.RayThrough(pt.X, pt.Y);
        }
        ray[n] = ray[0];
        for (int k = n - 1; k >= 0; k--)
        {
            Vector3 normal = Vector3.Cross(ray[k + 1], ray[k]);
            if (MathF.Abs(normal.X) >= WalkVisibilityMath.Epsilon
                || MathF.Abs(normal.Y) >= WalkVisibilityMath.Epsilon
                || MathF.Abs(normal.Z) >= WalkVisibilityMath.Epsilon)
            {
                normal *= 1f / MathF.Sqrt(
                    normal.X * normal.X + normal.Y * normal.Y + normal.Z * normal.Z);
            }
            WalkViewVertex v = view.Vertices[vbase + k];
            v.Plane = new WalkPlane(normal, -Vector3.Dot(normal, viewpoint));
            view.Vertices[vbase + k] = v;
        }

        dest.ViewCount += 1;
        return true;
    }
}
