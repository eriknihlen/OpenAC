using System.Numerics;

namespace AcDream.Core.Selection;

public static class WorldPicker
{
    public static (Vector3 Origin, Vector3 Direction) BuildRay(
        float mouseX,
        float mouseY,
        float viewportW,
        float viewportH,
        Matrix4x4 view,
        Matrix4x4 projection)
    {
        float ndcX = (2f * mouseX) / viewportW - 1f;
        float ndcY = 1f - (2f * mouseY) / viewportH;

        Matrix4x4 vp = view * projection;
        if (!Matrix4x4.Invert(vp, out Matrix4x4 invVp)
            || !Matrix4x4.Invert(view, out Matrix4x4 invView))
            return (Vector3.Zero, Vector3.Zero);

        Vector4 nearClip = new(ndcX, ndcY, -1f, 1f);
        Vector4 farClip = new(ndcX, ndcY, 1f, 1f);
        Vector4 near = Vector4.Transform(nearClip, invVp);
        Vector4 far = Vector4.Transform(farClip, invVp);
        if (near.W == 0f || far.W == 0f)
            return (Vector3.Zero, Vector3.Zero);

        Vector3 nearWorld = new Vector3(near.X, near.Y, near.Z) / near.W;
        Vector3 farWorld = new Vector3(far.X, far.Y, far.Z) / far.W;
        Vector3 direction = farWorld - nearWorld;
        if (direction.LengthSquared() < 1e-10f)
            return (Vector3.Zero, Vector3.Zero);

        Vector3 viewpoint = Vector3.Transform(Vector3.Zero, invView);
        return (viewpoint, Vector3.Normalize(direction));
    }
}
