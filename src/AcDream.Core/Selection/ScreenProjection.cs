using System.Numerics;

namespace AcDream.Core.Selection;

public static class ScreenProjection
{
    public static bool TryProjectSphereToScreenRect(
        Vector3 worldCenter, float worldRadius,
        Matrix4x4 view, Matrix4x4 projection, Vector2 viewport,
        out Vector2 rectMin, out Vector2 rectMax, out float depth,
        float minSidePixels = 12f)
    {
        rectMin = default;
        rectMax = default;
        depth   = 0f;

        var viewProj = view * projection;
        var clip = Vector4.Transform(new Vector4(worldCenter, 1f), viewProj);
        if (clip.W <= 0.001f) return false;

        depth = clip.W;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        float screenX = (ndcX * 0.5f + 0.5f) * viewport.X;
        float screenY = (1f - (ndcY * 0.5f + 0.5f)) * viewport.Y;

        float scaleY = projection.M22;
        if (scaleY <= 0f) return false;
        float screenRadius = worldRadius * scaleY * viewport.Y / (2f * clip.W);

        if (screenX + screenRadius < -viewport.X || screenX - screenRadius > 2f * viewport.X) return false;
        if (screenY + screenRadius < -viewport.Y || screenY - screenRadius > 2f * viewport.Y) return false;

        // Optional presentation floor for very distant spheres.
        if (screenRadius < minSidePixels * 0.5f) screenRadius = minSidePixels * 0.5f;

        rectMin = new Vector2(screenX - screenRadius, screenY - screenRadius);
        rectMax = new Vector2(screenX + screenRadius, screenY + screenRadius);
        return true;
    }
}
